using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SeatFlow.Messaging.Consuming;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox (writer + relay), the inbox dispatcher and the queue consumer for a service whose state
    /// lives in <typeparamref name="TDbContext"/>. Remember to call <c>UseOutboxSignal</c> on the DbContext options and
    /// <c>AddOutboxAndInbox</c> on its model.
    /// </summary>
    public static MessagingBuilder<TDbContext> AddMessaging<TDbContext>(this IHostApplicationBuilder builder)
        where TDbContext : DbContext
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("RabbitMq") ?? o.ConnectionString;
                if (string.IsNullOrWhiteSpace(o.QueueName))
                {
                    o.QueueName = builder.Environment.ApplicationName.ToLowerInvariant();
                }
            });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MessageTypeRegistry>();
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<OutboxSignal>();
        services.TryAddSingleton<OutboxSignalInterceptor>();
        services.AddScoped<IOutbox, EfOutbox<TDbContext>>();
        services.AddScoped<InboxDispatcher<TDbContext>>();

        var registry = new SubscriptionRegistry();
        services.AddSingleton(registry);
        services.AddHostedService<OutboxRelay<TDbContext>>();
        services.AddHostedService<RabbitMqConsumer>();

        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

        return new MessagingBuilder<TDbContext>(services, registry);
    }

    /// <summary>Lets the outbox relay publish right after commit instead of waiting for its next poll.</summary>
    public static DbContextOptionsBuilder UseOutboxSignal(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<OutboxSignalInterceptor>());
}

public sealed class MessagingBuilder<TDbContext>
    where TDbContext : DbContext
{
    private readonly IServiceCollection _services;
    private readonly SubscriptionRegistry _registry;
    private readonly MessageTypeRegistry _types = new();

    internal MessagingBuilder(IServiceCollection services, SubscriptionRegistry registry)
    {
        _services = services;
        _registry = registry;
    }

    public MessagingBuilder<TDbContext> Subscribe<TMessage, THandler>()
        where TMessage : class
        where THandler : class, IMessageHandler<TMessage>
    {
        var descriptor = _types.Get(typeof(TMessage));
        _services.AddScoped<IMessageHandler<TMessage>, THandler>();
        _registry.Subscriptions[descriptor.Name] = new MessageSubscription(descriptor, InvokeAsync<TMessage>);
        return this;
    }

    private static Task InvokeAsync<TMessage>(
        IServiceProvider services,
        object message,
        MessageContext context,
        CancellationToken cancellationToken)
        where TMessage : class =>
        services.GetRequiredService<InboxDispatcher<TDbContext>>().DispatchAsync(
            (TMessage)message,
            context,
            services.GetRequiredService<IMessageHandler<TMessage>>(),
            cancellationToken);
}
