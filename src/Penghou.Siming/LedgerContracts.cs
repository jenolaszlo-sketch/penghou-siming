namespace Penghou.Siming;

/// <summary>Provider-neutral append-only ledger operations.</summary>
public interface IAppendOnlyLedger
{
    /// <summary>Atomically appends definitive payload bytes.</summary>
    ValueTask<LedgerEntry> AppendAsync(LedgerAppendRequest request, CancellationToken cancellationToken = default);
    /// <summary>Finds an entry by its ledger-wide idempotency key.</summary>
    ValueTask<LedgerEntry?> ReadByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    /// <summary>Reads all entries, optionally restricted to a logical stream.</summary>
    IAsyncEnumerable<LedgerEntry> ReadAsync(string? streamId = null, CancellationToken cancellationToken = default);
    /// <summary>Reads one ordered page of entries.</summary>
    IAsyncEnumerable<LedgerEntry> ReadAsync(LedgerReadRequest request, CancellationToken cancellationToken = default);
    /// <summary>Gets the current global ledger head.</summary>
    ValueTask<LedgerHead> GetHeadAsync(CancellationToken cancellationToken = default);
    /// <summary>Verifies the ledger and an optional independently retained checkpoint.</summary>
    ValueTask<LedgerVerificationResult> VerifyAsync(LedgerCheckpoint? checkpoint = null, CancellationToken cancellationToken = default);
}

/// <summary>Specifies an ordered ledger page.</summary>
/// <param name="StreamId">Optional logical stream filter.</param>
/// <param name="AfterSequence">Exclusive global sequence cursor.</param>
/// <param name="Limit">Maximum entries to return.</param>
public sealed record LedgerReadRequest(
    string? StreamId = null,
    long AfterSequence = 0,
    int Limit = 100)
{
    /// <summary>Largest supported page size.</summary>
    public const int MaximumLimit = 10_000;

    /// <summary>Validates cursor, limit, and stream values.</summary>
    public void Validate()
    {
        if (AfterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(AfterSequence));
        if (Limit is <= 0 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(Limit));
        if (StreamId is not null && string.IsNullOrWhiteSpace(StreamId))
            throw new ArgumentException("Stream ID cannot be empty.", nameof(StreamId));
    }
}

/// <summary>Ledger operations with typed payload serialization.</summary>
/// <typeparam name="TSerializer">Configured payload serializer type.</typeparam>
public interface IAppendOnlyLedger<TSerializer> : IAppendOnlyLedger where TSerializer : ILedgerPayloadSerializer
{
    /// <summary>Serializes and atomically appends a typed payload.</summary>
    ValueTask<LedgerEntry> AppendAsync<T>(LedgerAppendRequest<T> request, CancellationToken cancellationToken = default);
}

/// <summary>Immutable identity of one ledger epoch.</summary>
/// <param name="Value">Underlying globally unique identifier.</param>
public readonly record struct LedgerId(Guid Value)
{
    /// <summary>Creates a new ledger identity.</summary>
    public static LedgerId New() => new(Guid.NewGuid());
}

/// <summary>Immutable 32-byte ledger hash.</summary>
public readonly struct LedgerHash : IEquatable<LedgerHash>
{
    /// <summary>Hash length in bytes.</summary>
    public const int Size = 32;
    private readonly byte[]? bytes;

    /// <summary>Creates a hash by copying exactly 32 bytes.</summary>
    public LedgerHash(ReadOnlySpan<byte> value)
    {
        if (value.Length != Size)
            throw new ArgumentException($"A SHA-256 ledger hash must contain {Size} bytes.", nameof(value));
        bytes = value.ToArray();
    }

    /// <summary>Gets the hash bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => bytes ?? new byte[Size];
    /// <inheritdoc />
    public bool Equals(LedgerHash other) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Bytes.Span, other.Bytes.Span);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LedgerHash other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Bytes.Span);
    /// <summary>Returns lowercase hexadecimal.</summary>
    public override string ToString() => Convert.ToHexString(Bytes.Span).ToLowerInvariant();
    /// <summary>Compares hashes in fixed time.</summary>
    public static bool operator ==(LedgerHash left, LedgerHash right) => left.Equals(right);
    /// <summary>Compares hashes in fixed time.</summary>
    public static bool operator !=(LedgerHash left, LedgerHash right) => !left.Equals(right);
}

/// <summary>One immutable globally ordered ledger entry.</summary>
public sealed record LedgerEntry(long Sequence, string StreamId, DateTimeOffset CommittedAt, string EventType, string ContentType, string SerializationFormat, int SerializationVersion, ReadOnlyMemory<byte> Payload, string? IdempotencyKey, LedgerHash PreviousHash, LedgerHash Hash, int FormatVersion);
/// <summary>Identity and hash of a ledger's current tail.</summary>
public sealed record LedgerHead(LedgerId LedgerId, long Sequence, LedgerHash Hash, int FormatVersion);
/// <summary>Portable externally retainable trusted ledger head.</summary>
public sealed record LedgerCheckpoint(LedgerId LedgerId, long Sequence, LedgerHash HeadHash, DateTimeOffset CreatedAt, int FormatVersion, string? Suite = null, string? KeyId = null);

/// <summary>Classifies a verification failure.</summary>
public enum LedgerVerificationFailure
{
    /// <summary>An expected global sequence is absent or out of order.</summary>
    SequenceGap,
    /// <summary>The first row does not reference the deterministic genesis.</summary>
    InvalidGenesis,
    /// <summary>A row does not reference the preceding verified hash.</summary>
    PreviousHashMismatch,
    /// <summary>A stored row hash differs from its recomputed value.</summary>
    RowHashMismatch,
    /// <summary>The ledger does not reach the checkpoint sequence.</summary>
    CheckpointSequenceMismatch,
    /// <summary>The chain hash at the checkpoint sequence differs.</summary>
    CheckpointHashMismatch,
    /// <summary>The checkpoint belongs to another ledger.</summary>
    LedgerIdentityMismatch,
    /// <summary>Persisted data cannot be decoded under its declared format.</summary>
    InvalidEncoding,
    /// <summary>The declared format version is unsupported.</summary>
    UnsupportedVersion,
    /// <summary>The verified chain does not match the independently captured head.</summary>
    HeadMismatch
}

/// <summary>Outcome and diagnostic state of ledger verification.</summary>
public sealed record LedgerVerificationResult(bool IsValid, long VerifiedEntries, LedgerHead VerifiedHead, long? FailedSequence = null, LedgerVerificationFailure? Failure = null, string? Detail = null);

/// <summary>Controls bounded verification.</summary>
public sealed record LedgerVerificationOptions(
    int PageSize = 1_000,
    IProgress<LedgerVerificationProgress>? Progress = null)
{
    /// <summary>Validates the configured page size.</summary>
    public void Validate()
    {
        if (PageSize is <= 0 or > LedgerReadRequest.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
    }
}

/// <summary>Reports a verified page boundary.</summary>
public sealed record LedgerVerificationProgress(
    long VerifiedEntries,
    long TargetEntries,
    LedgerHash VerifiedHash);
