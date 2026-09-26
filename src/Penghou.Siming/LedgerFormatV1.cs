using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

/// <summary>Defines the independently reproducible Penghou.Siming hash format v1.</summary>
public static class LedgerFormatV1
{
    /// <summary>Version committed into every v1 row.</summary>
    public const int Version = 1;
    /// <summary>Deterministic hash preceding the first row.</summary>
    public static LedgerHash GenesisHash { get; } = new(SHA256.HashData(Encoding.UTF8.GetBytes("penghou-siming-ledger-v1")));

    /// <summary>Computes a v1 row hash from all committed envelope fields.</summary>
    public static LedgerHash ComputeHash(LedgerId ledgerId, long sequence, DateTimeOffset committedAt, string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, LedgerHash previousHash)
    {
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
