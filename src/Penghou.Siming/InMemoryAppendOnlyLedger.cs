using System.Runtime.CompilerServices;

namespace Penghou.Siming;

/// <summary>In-memory ledger provider intended for testing and ephemeral use.</summary>
public sealed class InMemoryAppendOnlyLedger<TSerializer> : IAppendOnlyLedger<TSerializer>, IAsyncDisposable where TSerializer : ILedgerPayloadSerializer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<LedgerEntry> entries = [];
    private readonly Dictionary<string, LedgerEntry> idempotentEntries = new(StringComparer.Ordinal);
    private readonly TSerializer serializer;
    private readonly TimeProvider timeProvider;
    private readonly LedgerInputLimits inputLimits;
    private readonly LedgerContext? ledgerContext;
    private readonly int formatVersion;
    private readonly LedgerHash genesis;

    /// <summary>Creates an in-memory ledger.</summary>
    public InMemoryAppendOnlyLedger(TSerializer serializer, TimeProvider? timeProvider = null, LedgerId? ledgerId = null, LedgerInputLimits? inputLimits = null, LedgerContext? ledgerContext = null)
    {
        this.serializer = serializer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.inputLimits = inputLimits ?? LedgerInputLimits.Default;
        this.inputLimits.Validate();
        this.ledgerContext = ledgerContext;
        ledgerContext?.Validate();
        formatVersion = ledgerContext is null ? LedgerFormatV1.Version : LedgerFormatV2.Version;
        genesis = ledgerContext is null
            ? LedgerFormatV1.GenesisHash
            : LedgerFormatV2.GenesisHash(ledgerContext);
        LedgerId = ledgerId ?? Penghou.Siming.LedgerId.New();
    }

    /// <summary>Gets this ledger's immutable identity.</summary>
    public LedgerId LedgerId { get; }

    /// <summary>Gets the ledger context, or null for an epoch-1 ledger.</summary>
    public LedgerContext? Context => ledgerContext;

    /// <inheritdoc />
    public ValueTask<LedgerEntry> AppendAsync<T>(LedgerAppendRequest<T> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendSerializedAsync(request.StreamId, request.EventType, serializer.Serialize(request.Payload), request.IdempotencyKey, request.ExpectedHead, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LedgerEntry> AppendAsync(LedgerAppendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendSerializedAsync(request.StreamId, request.EventType, new SerializedLedgerPayload(request.Payload, request.ContentType, request.SerializationFormat, request.SerializationVersion), request.IdempotencyKey, request.ExpectedHead, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<LedgerEntry?> ReadByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        inputLimits.ValidateIdempotencyKey(idempotencyKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return idempotentEntries.GetValueOrDefault(idempotencyKey); }
        finally { gate.Release(); }
    }

    private async ValueTask<LedgerEntry> AppendSerializedAsync(string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, LedgerHead? expectedHead, CancellationToken cancellationToken)
    {
        inputLimits.ValidateAppend(streamId, eventType, payload, idempotencyKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (idempotencyKey is not null && idempotentEntries.TryGetValue(idempotencyKey, out var existing))
            {
                if (!Matches(existing, streamId, eventType, payload))
                    throw new LedgerIdempotencyConflictException(idempotencyKey);
                return existing;
            }
            var actualHead = entries.Count == 0
                ? new LedgerHead(LedgerId, 0, genesis, formatVersion)
                : new LedgerHead(LedgerId, entries[^1].Sequence, entries[^1].Hash, formatVersion);
            if (expectedHead is not null && expectedHead != actualHead)
                throw new LedgerHeadConflictException(expectedHead, actualHead);
            var sequence = entries.Count + 1L;
            var previous = entries.Count == 0 ? genesis : entries[^1].Hash;
            var committedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var definitivePayload = payload with { Bytes = payload.Bytes.ToArray() };
            var hash = ledgerContext is null
                ? LedgerFormatV1.ComputeHash(LedgerId, sequence, committedAt, streamId, eventType, definitivePayload, idempotencyKey, previous)
                : LedgerFormatV2.ComputeHash(LedgerId, sequence, committedAt, streamId, eventType, definitivePayload, idempotencyKey, previous, ledgerContext);
            var entry = new LedgerEntry(sequence, streamId, committedAt, eventType, definitivePayload.ContentType, definitivePayload.SerializationFormat, definitivePayload.SerializationVersion, definitivePayload.Bytes, idempotencyKey, previous, hash, formatVersion);
            entries.Add(entry);
            if (idempotencyKey is not null) idempotentEntries.Add(idempotencyKey, entry);
            return entry;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LedgerEntry> ReadAsync(string? streamId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LedgerEntry[] snapshot;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { snapshot = entries.Where(item => streamId is null || item.StreamId.Equals(streamId, StringComparison.Ordinal)).ToArray(); }
        finally { gate.Release(); }
        foreach (var entry in snapshot) { cancellationToken.ThrowIfCancellationRequested(); yield return entry; }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LedgerEntry> ReadAsync(LedgerReadRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        LedgerEntry[] snapshot;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = entries
                .Where(item => item.Sequence > request.AfterSequence &&
                    (request.StreamId is null || item.StreamId.Equals(request.StreamId, StringComparison.Ordinal)))
                .Take(request.Limit)
                .ToArray();
        }
        finally { gate.Release(); }
        foreach (var entry in snapshot) { cancellationToken.ThrowIfCancellationRequested(); yield return entry; }
    }

    /// <inheritdoc />
    public async ValueTask<LedgerHead> GetHeadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return entries.Count == 0 ? new(LedgerId, 0, genesis, formatVersion) : new(LedgerId, entries[^1].Sequence, entries[^1].Hash, formatVersion); }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<LedgerVerificationResult> VerifyAsync(LedgerCheckpoint? checkpoint = null, CancellationToken cancellationToken = default)
    {
        return await LedgerVerifier.VerifyAsync(
            this, checkpoint, cancellationToken: cancellationToken, context: ledgerContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { gate.Dispose(); return ValueTask.CompletedTask; }

    private static bool Matches(LedgerEntry entry, string streamId, string eventType, SerializedLedgerPayload payload) =>
        entry.StreamId.Equals(streamId, StringComparison.Ordinal) &&
        entry.EventType.Equals(eventType, StringComparison.Ordinal) &&
        entry.ContentType.Equals(payload.ContentType, StringComparison.Ordinal) &&
        entry.SerializationFormat.Equals(payload.SerializationFormat, StringComparison.Ordinal) &&
        entry.SerializationVersion == payload.SerializationVersion &&
        entry.Payload.Span.SequenceEqual(payload.Bytes.Span);
}
