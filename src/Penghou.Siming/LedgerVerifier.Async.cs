namespace Penghou.Siming;

public static partial class LedgerVerifier
{
    /// <summary>Verifies a provider using bounded pages, progress, and cancellation.</summary>
    public static async ValueTask<LedgerVerificationResult> VerifyAsync(
        IAppendOnlyLedger ledger,
        LedgerCheckpoint? checkpoint = null,
        LedgerVerificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        options ??= new LedgerVerificationOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var target = await ledger.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        return await VerifySnapshotAsync(
            target,
            (request, token) => ledger.ReadAsync(request, token),
            checkpoint,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<LedgerVerificationResult> VerifySnapshotAsync(
        LedgerHead target,
        Func<LedgerReadRequest, CancellationToken, IAsyncEnumerable<LedgerEntry>> read,
        LedgerCheckpoint? checkpoint,
        LedgerVerificationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var state = new State(target.LedgerId, checkpoint, options.Progress);
        var start = state.Start(target);
        if (start is not null) return start;
        var verifiedEntries = new List<LedgerEntry>(options.PageSize);
        state.ReportProgress(target.Sequence);

        while (state.Verified < target.Sequence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = (int)Math.Min(options.PageSize, target.Sequence - state.Verified);
            verifiedEntries.Clear();
            await foreach (var entry in read(
                               new LedgerReadRequest(AfterSequence: state.Verified, Limit: limit),
                               cancellationToken).ConfigureAwait(false))
                verifiedEntries.Add(entry);

            if (verifiedEntries.Count == 0)
                return state.HeadGap();

            foreach (var entry in verifiedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = state.Accept(entry);
                if (failure is not null) return failure;
            }
            state.ReportProgress(target.Sequence);
        }

        return state.Complete(target);
    }

    private static (LedgerVerificationFailure Failure, string Detail)?
        VerifyIncrementalEntry(
            LedgerId ledgerId,
            LedgerEntry entry,
            long expectedSequence,
            LedgerHash previous)
    {
        if (entry.FormatVersion != LedgerFormatV1.Version)
            return (LedgerVerificationFailure.UnsupportedVersion,
                $"Format version {entry.FormatVersion} is unsupported.");
        if (entry.Sequence != expectedSequence)
            return (LedgerVerificationFailure.SequenceGap,
                $"Expected sequence {expectedSequence}.");
        if (entry.PreviousHash != previous)
            return (entry.Sequence == 1
                    ? LedgerVerificationFailure.InvalidGenesis
                    : LedgerVerificationFailure.PreviousHashMismatch,
                "The previous hash does not match the verified chain head.");
        var payload = new SerializedLedgerPayload(entry.Payload, entry.ContentType,
            entry.SerializationFormat, entry.SerializationVersion);
        var calculated = LedgerFormatV1.ComputeHash(
            ledgerId, entry.Sequence, entry.CommittedAt, entry.StreamId,
            entry.EventType, payload, entry.IdempotencyKey, previous);
        return calculated == entry.Hash
            ? null
            : (LedgerVerificationFailure.RowHashMismatch,
                "The stored row hash does not match its committed fields.");
    }
}
