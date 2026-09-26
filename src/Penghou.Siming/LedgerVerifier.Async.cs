namespace Penghou.Siming;

public static partial class LedgerVerifier
{
    /// <summary>Verifies a provider using bounded pages, progress, and cancellation.</summary>
    public static async ValueTask<LedgerVerificationResult> VerifyAsync(
        IAppendOnlyLedger ledger,
        LedgerCheckpoint? checkpoint = null,
        LedgerVerificationOptions? options = null,
        CancellationToken cancellationToken = default,
        LedgerContext? context = null)
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
            cancellationToken,
            context).ConfigureAwait(false);
    }

    internal static async ValueTask<LedgerVerificationResult> VerifySnapshotAsync(
        LedgerHead target,
        Func<LedgerReadRequest, CancellationToken, IAsyncEnumerable<LedgerEntry>> read,
        LedgerCheckpoint? checkpoint,
        LedgerVerificationOptions options,
        CancellationToken cancellationToken,
        LedgerContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var state = new State(target.LedgerId, checkpoint, options.Progress, context);
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
            LedgerHash previous,
            LedgerContext? context)
    {
        var formatVersion = context is null ? LedgerFormatV1.Version : LedgerFormatV2.Version;
        if (entry.FormatVersion != formatVersion)
            return (LedgerVerificationFailure.UnsupportedVersion,
                $"Format version {entry.FormatVersion} is unsupported in epoch {formatVersion}.");
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
        var calculated = context is null
            ? LedgerFormatV1.ComputeHash(
                ledgerId, entry.Sequence, entry.CommittedAt, entry.StreamId,
                entry.EventType, payload, entry.IdempotencyKey, previous)
            : LedgerFormatV2.ComputeHash(
                ledgerId, entry.Sequence, entry.CommittedAt, entry.StreamId,
                entry.EventType, payload, entry.IdempotencyKey, previous, context);
        return calculated == entry.Hash
            ? null
            : (LedgerVerificationFailure.RowHashMismatch,
                "The stored row hash does not match its committed fields.");
    }
}
