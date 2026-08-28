using System.Runtime.CompilerServices;

namespace Penghou.Siming;

public sealed class InMemoryAppendOnlyLedger<TSerializer> : IAppendOnlyLedger<TSerializer>, IAsyncDisposable where TSerializer : ILedgerPayloadSerializer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<LedgerEntry> entries = [];
    private readonly Dictionary<string, LedgerEntry> idempotentEntries = new(StringComparer.Ordinal);
    private readonly TSerializer serializer;
    private readonly TimeProvider timeProvider;

    public InMemoryAppendOnlyLedger(TSerializer serializer, TimeProvider? timeProvider = null, LedgerId? ledgerId = null)
    {
        this.serializer = serializer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        LedgerId = ledgerId ?? Penghou.Siming.LedgerId.New();
    }

    public LedgerId LedgerId { get; }

    public ValueTask<LedgerEntry> AppendAsync<T>(LedgerAppendRequest<T> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendSerializedAsync(request.StreamId, request.EventType, serializer.Serialize(request.Payload), request.IdempotencyKey, cancellationToken);
    }

    public ValueTask<LedgerEntry> AppendAsync(LedgerAppendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendSerializedAsync(request.StreamId, request.EventType, new SerializedLedgerPayload(request.Payload, request.ContentType, request.SerializationFormat, request.SerializationVersion), request.IdempotencyKey, cancellationToken);
    }

    private async ValueTask<LedgerEntry> AppendSerializedAsync(string streamId, string eventType, SerializedLedgerPayload payload, string? idempotencyKey, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (idempotencyKey is not null && idempotentEntries.TryGetValue(idempotencyKey, out var existing))
            {
                if (!Matches(existing, streamId, eventType, payload))
                    throw new LedgerIdempotencyConflictException(idempotencyKey);
                return existing;
            }
            var sequence = entries.Count + 1L;
            var previous = entries.Count == 0 ? LedgerFormatV1.GenesisHash : entries[^1].Hash;
            var committedAt = timeProvider.GetUtcNow();
            var definitivePayload = payload with { Bytes = payload.Bytes.ToArray() };
            var hash = LedgerFormatV1.ComputeHash(LedgerId, sequence, committedAt, streamId, eventType, definitivePayload, idempotencyKey, previous);
            var entry = new LedgerEntry(sequence, streamId, committedAt, eventType, definitivePayload.ContentType, definitivePayload.SerializationFormat, definitivePayload.SerializationVersion, definitivePayload.Bytes, idempotencyKey, previous, hash, LedgerFormatV1.Version);
            entries.Add(entry);
            if (idempotencyKey is not null) idempotentEntries.Add(idempotencyKey, entry);
            return entry;
        }
        finally { gate.Release(); }
    }

    public async IAsyncEnumerable<LedgerEntry> ReadAsync(string? streamId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LedgerEntry[] snapshot;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { snapshot = entries.Where(item => streamId is null || item.StreamId.Equals(streamId, StringComparison.Ordinal)).ToArray(); }
        finally { gate.Release(); }
        foreach (var entry in snapshot) { cancellationToken.ThrowIfCancellationRequested(); yield return entry; }
    }

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

    public async ValueTask<LedgerHead> GetHeadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return entries.Count == 0 ? new(LedgerId, 0, LedgerFormatV1.GenesisHash, LedgerFormatV1.Version) : new(LedgerId, entries[^1].Sequence, entries[^1].Hash, LedgerFormatV1.Version); }
        finally { gate.Release(); }
    }

    public async ValueTask<LedgerVerificationResult> VerifyAsync(LedgerCheckpoint? checkpoint = null, CancellationToken cancellationToken = default)
    {
        return await LedgerVerifier.VerifyAsync(
            this, checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() { gate.Dispose(); return ValueTask.CompletedTask; }

    private static bool Matches(LedgerEntry entry, string streamId, string eventType, SerializedLedgerPayload payload) =>
        entry.StreamId.Equals(streamId, StringComparison.Ordinal) &&
        entry.EventType.Equals(eventType, StringComparison.Ordinal) &&
        entry.ContentType.Equals(payload.ContentType, StringComparison.Ordinal) &&
        entry.SerializationFormat.Equals(payload.SerializationFormat, StringComparison.Ordinal) &&
        entry.SerializationVersion == payload.SerializationVersion &&
        entry.Payload.Span.SequenceEqual(payload.Bytes.Span);
}
