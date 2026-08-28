namespace Penghou.Siming.Testing;

/// <summary>Outcome of provider-neutral behavioral conformance checks.</summary>
public sealed record LedgerProviderConformanceResult(
    bool Passed,
    IReadOnlyList<string> Checks,
    string? Failure = null);

/// <summary>
/// Provider-neutral behavioral checks. A backend supplies a fresh ledger and
/// remains responsible for disposing and deleting its storage afterward.
/// </summary>
/// <summary>Reusable behavioral contract for Siming storage providers.</summary>
public static class LedgerProviderConformance
{
    /// <summary>Runs the complete conformance suite against a fresh provider.</summary>
    public static async ValueTask<LedgerProviderConformanceResult> RunAsync(
        Func<CancellationToken, ValueTask<IAppendOnlyLedger>> createLedger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createLedger);
        var checks = new List<string>();
        try
        {
            var ledger = await createLedger(cancellationToken).ConfigureAwait(false);
            var empty = await ledger.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            Require(empty.Sequence == 0 && empty.Hash == LedgerFormatV1.GenesisHash, "empty head");
            checks.Add("empty-head");

            var first = await ledger.AppendAsync(
                new LedgerAppendRequest(
                    "stream-a",
                    "one",
                    new byte[] { 1 },
                    IdempotencyKey: "conformance:first"),
                cancellationToken).ConfigureAwait(false);
            var second = await ledger.AppendAsync(
                new LedgerAppendRequest("stream-b", "two", new byte[] { 2 }),
                cancellationToken).ConfigureAwait(false);
            Require(first.Sequence == 1 && second.Sequence == 2, "global sequence");
            Require(second.PreviousHash == first.Hash, "hash continuity");
            checks.Add("global-chain");

            var replay = await ledger.AppendAsync(
                new LedgerAppendRequest(
                    "stream-a",
                    "one",
                    new byte[] { 1 },
                    IdempotencyKey: "conformance:first"),
                cancellationToken).ConfigureAwait(false);
            Require(replay.Sequence == first.Sequence && replay.Hash == first.Hash, "idempotent replay");
            var found = await ledger.ReadByIdempotencyKeyAsync(
                "conformance:first",
                cancellationToken).ConfigureAwait(false);
            Require(found?.Sequence == first.Sequence && found.Hash == first.Hash, "idempotency lookup");
            Require(await ledger.ReadByIdempotencyKeyAsync(
                "conformance:missing",
                cancellationToken).ConfigureAwait(false) is null, "missing idempotency lookup");
            try
            {
                await ledger.AppendAsync(
                    new LedgerAppendRequest(
                        "stream-a",
                        "one",
                        new byte[] { 9 },
                        IdempotencyKey: "conformance:first"),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Ledger provider accepted conflicting idempotent content.");
            }
            catch (LedgerIdempotencyConflictException)
            {
            }
            checks.Add("idempotency");

            var page = await ReadAsync(
                ledger.ReadAsync(new LedgerReadRequest(AfterSequence: 1, Limit: 1), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            Require(page.Count == 1 && page[0].Sequence == 2, "paged read");
            var stream = await ReadAsync(ledger.ReadAsync("stream-a", cancellationToken), cancellationToken).ConfigureAwait(false);
            Require(stream.Count == 1 && stream[0].Sequence == 1, "stream filter");
            checks.Add("queries");

            var progress = new RecordingProgress();
            var verification = await LedgerVerifier.VerifyAsync(
                ledger,
                options: new LedgerVerificationOptions(PageSize: 1, Progress: progress),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Require(verification.IsValid && verification.VerifiedEntries == 2, "verification");
            Require(progress.Values.Select(item => item.VerifiedEntries)
                .SequenceEqual([0L, 1L, 2L]), "bounded verification progress");
            checks.Add("bounded-verification");

            using var cancelled = new CancellationTokenSource();
            var cancellingProgress = new CallbackProgress(value =>
            {
                if (value.VerifiedEntries == 1)
                    cancelled.Cancel();
            });
            try
            {
                await LedgerVerifier.VerifyAsync(
                    ledger,
                    options: new LedgerVerificationOptions(1, cancellingProgress),
                    cancellationToken: cancelled.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Ledger verification ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
            {
            }
            checks.Add("verification-cancellation");
            return new(true, checks);
        }
        catch (Exception exception)
        {
            return new(false, checks, exception.Message);
        }
    }

    private static async Task<List<LedgerEntry>> ReadAsync(
        IAsyncEnumerable<LedgerEntry> source,
        CancellationToken cancellationToken)
    {
        var result = new List<LedgerEntry>();
        await foreach (var entry in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            result.Add(entry);
        return result;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new InvalidOperationException($"Ledger provider failed the {check} conformance check.");
    }

    private sealed class RecordingProgress : IProgress<LedgerVerificationProgress>
    {
        public List<LedgerVerificationProgress> Values { get; } = [];
        public void Report(LedgerVerificationProgress value) => Values.Add(value);
    }

    private sealed class CallbackProgress(Action<LedgerVerificationProgress> callback) :
        IProgress<LedgerVerificationProgress>
    {
        public void Report(LedgerVerificationProgress value) => callback(value);
    }
}
