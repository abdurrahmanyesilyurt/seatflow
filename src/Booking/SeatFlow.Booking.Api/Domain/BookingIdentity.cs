using System.Security.Cryptography;
using System.Text;

namespace SeatFlow.Bookings.Domain;

/// <summary>
/// The booking id is derived from (user, Idempotency-Key) as an RFC 4122 v5 UUID. Two concurrent retries of the
/// same request therefore compute the same id: they share the same Redis lock owner (the lock script is re-entrant)
/// and collide on the primary key, so exactly one booking is created and the other request replays it.
/// </summary>
public static class BookingIdentity
{
    private static readonly Guid BookingNamespace = new("4f8d1e9c-7a2b-4c3d-9e5f-1a2b3c4d5e6f");

    public static Guid FromIdempotencyKey(string userId, string idempotencyKey) =>
        CreateV5(BookingNamespace, $"{userId}\n{idempotencyKey}");

    public static string RequestHash(Guid eventId, IEnumerable<string> seatIds)
    {
        var canonical = $"{eventId}|{string.Join(',', seatIds.Order(StringComparer.Ordinal))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static Guid CreateV5(Guid namespaceId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        // SHA-1 is what RFC 4122 specifies for v5; this is an identifier, not a security boundary.
#pragma warning disable CA5350
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash[..16], bigEndian: true);
    }
}
