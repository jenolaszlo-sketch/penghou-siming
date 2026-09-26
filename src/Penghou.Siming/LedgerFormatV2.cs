using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

/// <summary>Defines the context-bound Penghou.Siming hash format v2.</summary>
/// <remarks>
/// v2 preserves the exact v1 row envelope field order and appends the 32-byte
/// canonical ledger-context digest after the previous hash, so every row and
/// the genesis commit to the external context. v1 rows are never reinterpreted.
/// </remarks>
public static class LedgerFormatV2
{
    /// <summary>Version committed into every v2 row.</summary>
    public const int Version = 2;

    private static readonly byte[] GenesisDomain =
        "penghou-siming-ledger-v2\0"u8.ToArray();

    /// <summary>Computes the deterministic genesis binding a ledger context.</summary>
    public static LedgerHash GenesisHash(LedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var digest = context.ComputeDigest();
        var input = new byte[GenesisDomain.Length + LedgerHash.Size];
        GenesisDomain.CopyTo(input, 0);
        digest.Bytes.Span.CopyTo(input.AsSpan(GenesisDomain.Length));
        return new LedgerHash(SHA256.HashData(input));
    }

    /// <summary>Computes a v2 row hash from all committed envelope fields and the context.</summary>
    public static LedgerHash ComputeHash(LedgerId ledgerId, long sequence, DateTimeOffset committedAt, string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, LedgerHash previousHash, LedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ValidateText(streamId, nameof(streamId)); ValidateText(eventType, nameof(eventType));
        ValidateText(payload.ContentType, nameof(payload.ContentType)); ValidateText(payload.SerializationFormat, nameof(payload.SerializationFormat));
        if (payload.SerializationVersion <= 0) throw new ArgumentOutOfRangeException(nameof(payload.SerializationVersion));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, Version);
        Span<byte> guidBytes = stackalloc byte[16]; ledgerId.Value.TryWriteBytes(guidBytes, bigEndian: true, out _); hash.AppendData(guidBytes);
        AppendInt64(hash, sequence); AppendInt64(hash, committedAt.ToUnixTimeMilliseconds());
        AppendText(hash, streamId); AppendText(hash, eventType); AppendText(hash, payload.ContentType); AppendText(hash, payload.SerializationFormat);
        AppendInt32(hash, payload.SerializationVersion); AppendBytes(hash, payload.Bytes.Span); AppendNullableText(hash, idempotencyKey); hash.AppendData(previousHash.Bytes.Span);
        hash.AppendData(context.ComputeDigest().Bytes.Span);
        return new LedgerHash(hash.GetHashAndReset());
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        AppendInt32(hash, byteCount);
        if (byteCount == 0) return;
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendNullableText(IncrementalHash hash, string? value) { if (value is null) { AppendInt32(hash, -1); return; } AppendText(hash, value); }
    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value) { AppendInt32(hash, value.Length); hash.AppendData(value); }
    private static void AppendInt32(IncrementalHash hash, int value) { Span<byte> span = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(span, value); hash.AppendData(span); }
    private static void AppendInt64(IncrementalHash hash, long value) { Span<byte> span = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(span, value); hash.AppendData(span); }
    private static void ValidateText(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be empty.", name); }
}
