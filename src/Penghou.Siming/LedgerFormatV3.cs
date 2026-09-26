using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

/// <summary>Defines the keyed Penghou.Siming hash format v3 (hmac-sha256-v1).</summary>
/// <remarks>
/// v3 streams the exact v2 row envelope field order through HMAC-SHA256 under
/// the external secret, binding the suite identity, key identifier, and
/// context digest alongside every field. Epoch-1 and epoch-2 rows are never
/// reinterpreted: verification without the exact secret fails closed.
/// </remarks>
public static class LedgerFormatV3
{
    /// <summary>Version committed into every v3 row.</summary>
    public const int Version = 3;

    /// <summary>Suite identity committed by every v3 hash and checkpoint.</summary>
    public const string Suite = "hmac-sha256-v1";

    private static readonly byte[] GenesisDomain =
        "penghou-siming-ledger-v3-genesis\0"u8.ToArray();
    private static readonly byte[] RowDomain =
        "penghou-siming-ledger-v3-row\0"u8.ToArray();

    /// <summary>Computes the deterministic genesis binding context, suite, key, and secret.</summary>
    public static LedgerHash GenesisHash(LedgerContext context, LedgerHmacKey key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        using var hash = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256, key.Secret.Span);
        Append(hash, GenesisDomain);
        AppendText(hash, Suite);
        AppendText(hash, key.KeyId);
        hash.AppendData(context.ComputeDigest().Bytes.Span);
        return new LedgerHash(hash.GetHashAndReset());
    }

    /// <summary>Computes a v3 row hash from all committed envelope fields, context, and secret.</summary>
    public static LedgerHash ComputeHash(LedgerId ledgerId, long sequence, DateTimeOffset committedAt, string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, LedgerHash previousHash, LedgerContext context, LedgerHmacKey key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ValidateText(streamId, nameof(streamId)); ValidateText(eventType, nameof(eventType));
        ValidateText(payload.ContentType, nameof(payload.ContentType)); ValidateText(payload.SerializationFormat, nameof(payload.SerializationFormat));
        if (payload.SerializationVersion <= 0) throw new ArgumentOutOfRangeException(nameof(payload.SerializationVersion));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        using var hash = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256, key.Secret.Span);
        Append(hash, RowDomain);
        AppendText(hash, Suite);
        AppendText(hash, key.KeyId);
        AppendInt32(hash, Version);
        Span<byte> guidBytes = stackalloc byte[16]; ledgerId.Value.TryWriteBytes(guidBytes, bigEndian: true, out _); hash.AppendData(guidBytes);
        AppendInt64(hash, sequence); AppendInt64(hash, committedAt.ToUnixTimeMilliseconds());
        AppendText(hash, streamId); AppendText(hash, eventType); AppendText(hash, payload.ContentType); AppendText(hash, payload.SerializationFormat);
        AppendInt32(hash, payload.SerializationVersion); AppendBytes(hash, payload.Bytes.Span); AppendNullableText(hash, idempotencyKey); hash.AppendData(previousHash.Bytes.Span);
        hash.AppendData(context.ComputeDigest().Bytes.Span);
        return new LedgerHash(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value) =>
        hash.AppendData(value);

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendNullableText(IncrementalHash hash, string? value) { if (value is null) { AppendInt32(hash, -1); return; } AppendText(hash, value); }
    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value) { AppendInt32(hash, value.Length); hash.AppendData(value); }
    private static void AppendInt32(IncrementalHash hash, int value) { Span<byte> span = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(span, value); hash.AppendData(span); }
    private static void AppendInt64(IncrementalHash hash, long value) { Span<byte> span = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(span, value); hash.AppendData(span); }
    private static void ValidateText(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be empty.", name); }
}
