using System.Text;

namespace Penghou.Siming;

/// <summary>Provider-neutral limits applied before an event is appended.</summary>
public sealed record LedgerInputLimits
{
    /// <summary>Gets a conservative set of default limits.</summary>
    public static LedgerInputLimits Default { get; } = new();

    /// <summary>Maximum serialized payload size in bytes.</summary>
    public int MaxPayloadBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum UTF-8 stream identifier size in bytes.</summary>
    public int MaxStreamIdUtf8Bytes { get; init; } = 1024;
    /// <summary>Maximum UTF-8 event type size in bytes.</summary>
    public int MaxEventTypeUtf8Bytes { get; init; } = 512;
    /// <summary>Maximum UTF-8 content type size in bytes.</summary>
    public int MaxContentTypeUtf8Bytes { get; init; } = 256;
    /// <summary>Maximum UTF-8 serialization format size in bytes.</summary>
    public int MaxSerializationFormatUtf8Bytes { get; init; } = 256;
    /// <summary>Maximum UTF-8 idempotency key size in bytes.</summary>
    public int MaxIdempotencyKeyUtf8Bytes { get; init; } = 1024;

    /// <summary>Validates that every configured maximum is positive.</summary>
    public void Validate()
    {
        Positive(MaxPayloadBytes, nameof(MaxPayloadBytes));
        Positive(MaxStreamIdUtf8Bytes, nameof(MaxStreamIdUtf8Bytes));
        Positive(MaxEventTypeUtf8Bytes, nameof(MaxEventTypeUtf8Bytes));
        Positive(MaxContentTypeUtf8Bytes, nameof(MaxContentTypeUtf8Bytes));
        Positive(MaxSerializationFormatUtf8Bytes, nameof(MaxSerializationFormatUtf8Bytes));
        Positive(MaxIdempotencyKeyUtf8Bytes, nameof(MaxIdempotencyKeyUtf8Bytes));
    }

    internal void ValidateAppend(string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey)
    {
        Check("payload", payload.Bytes.Length, MaxPayloadBytes);
        CheckUtf8("streamId", streamId, MaxStreamIdUtf8Bytes);
        CheckUtf8("eventType", eventType, MaxEventTypeUtf8Bytes);
        CheckUtf8("contentType", payload.ContentType, MaxContentTypeUtf8Bytes);
        CheckUtf8("serializationFormat", payload.SerializationFormat, MaxSerializationFormatUtf8Bytes);
        if (idempotencyKey is not null) CheckUtf8("idempotencyKey", idempotencyKey, MaxIdempotencyKeyUtf8Bytes);
    }

    internal void ValidateIdempotencyKey(string idempotencyKey) =>
        CheckUtf8("idempotencyKey", idempotencyKey, MaxIdempotencyKeyUtf8Bytes);

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, value, "Input limits must be positive.");
    }

    private static void CheckUtf8(string field, string value, int maximum) => Check(field, Encoding.UTF8.GetByteCount(value), maximum);
    private static void Check(string field, int actual, int maximum)
    {
        if (actual > maximum) throw new LedgerInputLimitExceededException(field, actual, maximum);
    }
}

/// <summary>Indicates that an append input exceeds a configured byte limit.</summary>
public sealed class LedgerInputLimitExceededException : ArgumentException
{
    /// <summary>Creates an input-limit error.</summary>
    public LedgerInputLimitExceededException(string fieldName, int actualBytes, int maximumBytes)
        : base($"Append field '{fieldName}' is {actualBytes} bytes; the configured maximum is {maximumBytes} bytes.", fieldName)
    {
        FieldName = fieldName;
        ActualBytes = actualBytes;
        MaximumBytes = maximumBytes;
    }

    /// <summary>Gets the rejected field's stable logical name.</summary>
    public string FieldName { get; }
    /// <summary>Gets its measured UTF-8 or binary size.</summary>
    public int ActualBytes { get; }
    /// <summary>Gets the configured maximum size.</summary>
    public int MaximumBytes { get; }
}
