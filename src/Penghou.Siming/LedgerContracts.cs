namespace Penghou.Siming;

public interface IAppendOnlyLedger
{
    ValueTask<LedgerEntry> AppendAsync(LedgerAppendRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<LedgerEntry> ReadAsync(string? streamId = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<LedgerEntry> ReadAsync(LedgerReadRequest request, CancellationToken cancellationToken = default);
    ValueTask<LedgerHead> GetHeadAsync(CancellationToken cancellationToken = default);
    ValueTask<LedgerVerificationResult> VerifyAsync(LedgerCheckpoint? checkpoint = null, CancellationToken cancellationToken = default);
}

public sealed record LedgerReadRequest(
    string? StreamId = null,
    long AfterSequence = 0,
    int Limit = 100)
{
    public const int MaximumLimit = 10_000;

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

public interface IAppendOnlyLedger<TSerializer> : IAppendOnlyLedger where TSerializer : ILedgerPayloadSerializer
{
    ValueTask<LedgerEntry> AppendAsync<T>(LedgerAppendRequest<T> request, CancellationToken cancellationToken = default);
}

public readonly record struct LedgerId(Guid Value)
{
    public static LedgerId New() => new(Guid.NewGuid());
}

public readonly struct LedgerHash : IEquatable<LedgerHash>
{
    public const int Size = 32;
    private readonly byte[]? bytes;

    public LedgerHash(ReadOnlySpan<byte> value)
    {
        if (value.Length != Size)
            throw new ArgumentException($"A SHA-256 ledger hash must contain {Size} bytes.", nameof(value));
        bytes = value.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => bytes ?? new byte[Size];
    public bool Equals(LedgerHash other) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Bytes.Span, other.Bytes.Span);
    public override bool Equals(object? obj) => obj is LedgerHash other && Equals(other);
    public override int GetHashCode() => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Bytes.Span);
    public override string ToString() => Convert.ToHexString(Bytes.Span).ToLowerInvariant();
    public static bool operator ==(LedgerHash left, LedgerHash right) => left.Equals(right);
    public static bool operator !=(LedgerHash left, LedgerHash right) => !left.Equals(right);
}

public sealed record LedgerEntry(long Sequence, string StreamId, DateTimeOffset CommittedAt, string EventType, string ContentType, string SerializationFormat, int SerializationVersion, ReadOnlyMemory<byte> Payload, string? IdempotencyKey, LedgerHash PreviousHash, LedgerHash Hash, int FormatVersion);
public sealed record LedgerHead(LedgerId LedgerId, long Sequence, LedgerHash Hash, int FormatVersion);
public sealed record LedgerCheckpoint(LedgerId LedgerId, long Sequence, LedgerHash HeadHash, DateTimeOffset CreatedAt, int FormatVersion);

public enum LedgerVerificationFailure
{
    SequenceGap, InvalidGenesis, PreviousHashMismatch, RowHashMismatch,
    CheckpointSequenceMismatch, CheckpointHashMismatch, LedgerIdentityMismatch,
    InvalidEncoding, UnsupportedVersion
}

public sealed record LedgerVerificationResult(bool IsValid, long VerifiedEntries, LedgerHead VerifiedHead, long? FailedSequence = null, LedgerVerificationFailure? Failure = null, string? Detail = null);

public sealed record LedgerVerificationOptions(
    int PageSize = 1_000,
    IProgress<LedgerVerificationProgress>? Progress = null)
{
    public void Validate()
    {
        if (PageSize is <= 0 or > LedgerReadRequest.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
    }
}

public sealed record LedgerVerificationProgress(
    long VerifiedEntries,
    long TargetEntries,
    LedgerHash VerifiedHash);
