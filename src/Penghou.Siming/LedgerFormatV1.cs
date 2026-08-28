using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

public static class LedgerFormatV1
{
    public const int Version = 1;
    public static LedgerHash GenesisHash { get; } = new(SHA256.HashData(Encoding.UTF8.GetBytes("penghou-siming-ledger-v1")));

    public static LedgerHash ComputeHash(LedgerId ledgerId, long sequence, DateTimeOffset committedAt, string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, LedgerHash previousHash)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ValidateText(streamId, nameof(streamId)); ValidateText(eventType, nameof(eventType));
        ValidateText(payload.ContentType, nameof(payload.ContentType)); ValidateText(payload.SerializationFormat, nameof(payload.SerializationFormat));
        if (payload.SerializationVersion <= 0) throw new ArgumentOutOfRangeException(nameof(payload.SerializationVersion));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        var buffer = new ArrayBufferWriter<byte>();
        WriteInt32(buffer, Version);
        Span<byte> guidBytes = stackalloc byte[16]; ledgerId.Value.TryWriteBytes(guidBytes, bigEndian: true, out _); Write(buffer, guidBytes);
        WriteInt64(buffer, sequence); WriteInt64(buffer, committedAt.ToUnixTimeMilliseconds());
        WriteText(buffer, streamId); WriteText(buffer, eventType); WriteText(buffer, payload.ContentType); WriteText(buffer, payload.SerializationFormat);
        WriteInt32(buffer, payload.SerializationVersion); WriteBytes(buffer, payload.Bytes.Span); WriteNullableText(buffer, idempotencyKey); Write(buffer, previousHash.Bytes.Span);
        return new LedgerHash(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteText(IBufferWriter<byte> writer, string value) => WriteBytes(writer, Encoding.UTF8.GetBytes(value));
    private static void WriteNullableText(IBufferWriter<byte> writer, string? value) { if (value is null) { WriteInt32(writer, -1); return; } WriteText(writer, value); }
    private static void WriteBytes(IBufferWriter<byte> writer, ReadOnlySpan<byte> value) { WriteInt32(writer, value.Length); Write(writer, value); }
    private static void WriteInt32(IBufferWriter<byte> writer, int value) { var span = writer.GetSpan(4); BinaryPrimitives.WriteInt32BigEndian(span, value); writer.Advance(4); }
    private static void WriteInt64(IBufferWriter<byte> writer, long value) { var span = writer.GetSpan(8); BinaryPrimitives.WriteInt64BigEndian(span, value); writer.Advance(8); }
    private static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> value) { value.CopyTo(writer.GetSpan(value.Length)); writer.Advance(value.Length); }
    private static void ValidateText(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be empty.", name); }
}
