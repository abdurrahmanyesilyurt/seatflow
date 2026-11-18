using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeatFlow.Contracts;

namespace SeatFlow.Messaging;

public sealed record MessageDescriptor(string Name, Type Type, bool Required);

/// <summary>Maps wire names (routing keys) to CLR types for every <see cref="MessageAttribute"/> in the contracts assembly.</summary>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<string, MessageDescriptor> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, MessageDescriptor> _byType = [];

    public MessageTypeRegistry() : this(typeof(MessageAttribute).Assembly)
    {
    }

    public MessageTypeRegistry(params Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetExportedTypes()))
        {
            if (type.GetCustomAttribute<MessageAttribute>() is not { } attribute)
            {
                continue;
            }

            var descriptor = new MessageDescriptor(attribute.Name, type, attribute.Required);
            if (!_byName.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException($"Message name '{descriptor.Name}' is used by more than one type.");
            }

            _byType.Add(type, descriptor);
        }
    }

    public MessageDescriptor Get(Type type) =>
        _byType.TryGetValue(type, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"{type.FullName} is not an integration message (missing [Message]).");

    public bool TryGet(string name, out MessageDescriptor descriptor) =>
        _byName.TryGetValue(name, out descriptor!);
}

public static class MessagingJson
{
    /// <summary>camelCase JSON with string enums: the contract the Python consumer reads.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
