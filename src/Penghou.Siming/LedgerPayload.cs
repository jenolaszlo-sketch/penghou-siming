namespace Penghou.Siming;

public sealed record SerializedLedgerPayload(ReadOnlyMemory<byte> Bytes, string ContentType, string SerializationFormat, int SerializationVersion);
public interface ILedgerPayloadSerializer { SerializedLedgerPayload Serialize<T>(T payload); }
public sealed record LedgerAppendRequest(string StreamId, string EventType, ReadOnlyMemory<byte> Payload, string ContentType = "application/octet-stream", string SerializationFormat = "raw", int SerializationVersion = 1, string? IdempotencyKey = null);
public sealed record LedgerAppendRequest<T>(string StreamId, string EventType, T Payload, string? IdempotencyKey = null);

public sealed class LedgerIdempotencyConflictException : Exception
{
    public LedgerIdempotencyConflictException(string idempotencyKey)
        : base($"Idempotency key '{idempotencyKey}' is already committed with different event content.") =>
        IdempotencyKey = idempotencyKey;

    public string IdempotencyKey { get; }
}
