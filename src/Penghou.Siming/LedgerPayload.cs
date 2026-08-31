namespace Penghou.Siming;

/// <summary>Definitive serialized payload bytes and their interpretation descriptors.</summary>
/// <param name="Bytes">Exact committed bytes.</param>
/// <param name="ContentType">Payload media type.</param>
/// <param name="SerializationFormat">Serializer format identifier.</param>
/// <param name="SerializationVersion">Serializer format version.</param>
public sealed record SerializedLedgerPayload(ReadOnlyMemory<byte> Bytes, string ContentType, string SerializationFormat, int SerializationVersion);
/// <summary>Serializes typed payloads into definitive ledger bytes.</summary>
public interface ILedgerPayloadSerializer
{
    /// <summary>Serializes one payload without persisting it.</summary>
    SerializedLedgerPayload Serialize<T>(T payload);
}
/// <summary>Requests an append using definitive serialized bytes.</summary>
/// <param name="StreamId">Logical stream identifier.</param>
/// <param name="EventType">Application event type.</param>
/// <param name="Payload">Exact payload bytes.</param>
/// <param name="ContentType">Payload media type.</param>
/// <param name="SerializationFormat">Serializer format identifier.</param>
/// <param name="SerializationVersion">Serializer version.</param>
/// <param name="IdempotencyKey">Optional ledger-wide idempotency key.</param>
/// <param name="ExpectedHead">Optional exact head required for a new append.</param>
public sealed record LedgerAppendRequest(string StreamId, string EventType, ReadOnlyMemory<byte> Payload, string ContentType = "application/octet-stream", string SerializationFormat = "raw", int SerializationVersion = 1, string? IdempotencyKey = null, LedgerHead? ExpectedHead = null);
/// <summary>Requests an append serialized by the configured serializer.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="StreamId">Logical stream identifier.</param>
/// <param name="EventType">Application event type.</param>
/// <param name="Payload">Typed payload.</param>
/// <param name="IdempotencyKey">Optional ledger-wide idempotency key.</param>
/// <param name="ExpectedHead">Optional exact head required for a new append.</param>
public sealed record LedgerAppendRequest<T>(string StreamId, string EventType, T Payload, string? IdempotencyKey = null, LedgerHead? ExpectedHead = null);

/// <summary>Indicates that an idempotency key has different committed content.</summary>
public sealed class LedgerIdempotencyConflictException : Exception
{
    /// <summary>Creates a conflict for the reused key.</summary>
    public LedgerIdempotencyConflictException(string idempotencyKey)
        : base($"Idempotency key '{idempotencyKey}' is already committed with different event content.") =>
        IdempotencyKey = idempotencyKey;

    /// <summary>Gets the conflicting idempotency key.</summary>
    public string IdempotencyKey { get; }
}

/// <summary>Indicates that a conditional append observed a different head.</summary>
public sealed class LedgerHeadConflictException : Exception
{
    /// <summary>Creates a conflict with the expected and atomically observed heads.</summary>
    public LedgerHeadConflictException(LedgerHead expectedHead, LedgerHead actualHead)
        : base(
            $"Ledger head changed: expected {expectedHead.LedgerId.Value:D}/" +
            $"{expectedHead.Sequence}/{expectedHead.Hash}, observed " +
            $"{actualHead.LedgerId.Value:D}/{actualHead.Sequence}/{actualHead.Hash}.")
    {
        ExpectedHead = expectedHead;
        ActualHead = actualHead;
    }

    /// <summary>Gets the head supplied by the caller.</summary>
    public LedgerHead ExpectedHead { get; }

    /// <summary>Gets the head atomically observed by the provider.</summary>
    public LedgerHead ActualHead { get; }
}
