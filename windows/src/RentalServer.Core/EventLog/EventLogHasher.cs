using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RentalServer.Core.EventLog;

/// <summary>
/// SHA-256 over a length-prefixed canonical concatenation of the hash-relevant
/// columns. Length-prefixing every field is what stops a sneaky attacker from
/// migrating bytes across field boundaries — e.g. moving the trailing digits
/// of station_id into the leading digits of acceptor_id would otherwise hash
/// to the same value. Each field is prefixed with a 4-byte big-endian
/// uint32 length. NULL serializes as length=0 + zero bytes, distinct from
/// an empty string that would also serialize as length=0 + zero bytes — we
/// accept that ambiguity intentionally because we never store empty strings
/// in nullable columns (callers pass null for "missing" and a real value
/// for "present"). The integer columns are formatted with
/// InvariantCulture so the hash is stable across machines.
/// </summary>
public static class EventLogHasher
{
    public const string GenesisPrevHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(
        long id,
        string ts,
        string eventType,
        string? stationId,
        string? acceptorId,
        int? amountPesos,
        int? secondsCredited,
        string payload,
        string prevHash)
    {
        using var buffer = new MemoryStream(capacity: 512);

        AppendField(buffer, FormatLong(id));
        AppendField(buffer, ts);
        AppendField(buffer, eventType);
        AppendField(buffer, stationId);
        AppendField(buffer, acceptorId);
        AppendField(buffer, amountPesos.HasValue ? FormatInt(amountPesos.Value) : null);
        AppendField(buffer, secondsCredited.HasValue ? FormatInt(secondsCredited.Value) : null);
        AppendField(buffer, payload);
        AppendField(buffer, prevHash);

        buffer.Position = 0;
        var hash = SHA256.HashData(buffer.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendField(MemoryStream buffer, string? value)
    {
        if (value is null)
        {
            // NULL marker: length=0, no payload bytes.
            Span<byte> lengthBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, 0);
            buffer.Write(lengthBytes);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }

    private static string FormatInt(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatLong(long value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
