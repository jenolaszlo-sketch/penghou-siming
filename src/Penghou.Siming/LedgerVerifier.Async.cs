namespace Penghou.Siming;

public static partial class LedgerVerifier
{
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
        if (checkpoint is not null && checkpoint.LedgerId != target.LedgerId)
            return new(false, 0,
                new LedgerHead(target.LedgerId, 0, LedgerFormatV1.GenesisHash,
                    LedgerFormatV1.Version),
                checkpoint.Sequence, LedgerVerificationFailure.LedgerIdentityMismatch,
                "The checkpoint belongs to another ledger.");
        if (checkpoint is not null && checkpoint.FormatVersion != LedgerFormatV1.Version)
            return new(false, 0,
                new LedgerHead(target.LedgerId, 0, LedgerFormatV1.GenesisHash,
                    LedgerFormatV1.Version),
                checkpoint.Sequence, LedgerVerificationFailure.UnsupportedVersion,
                "The checkpoint format version is unsupported.");
        var verifiedEntries = new List<LedgerEntry>(options.PageSize);
        var previous = LedgerFormatV1.GenesisHash;
        long verified = 0;
        options.Progress?.Report(new(verified, target.Sequence, previous));

        while (verified < target.Sequence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = (int)Math.Min(options.PageSize, target.Sequence - verified);
            verifiedEntries.Clear();
            await foreach (var entry in ledger.ReadAsync(
                               new LedgerReadRequest(AfterSequence: verified, Limit: limit),
                               cancellationToken).ConfigureAwait(false))
                verifiedEntries.Add(entry);

            if (verifiedEntries.Count == 0)
                return new(false, verified,
                    new LedgerHead(target.LedgerId, verified, previous, LedgerFormatV1.Version),
                    verified + 1, LedgerVerificationFailure.SequenceGap,
                    "The captured ledger head cannot be reached from persisted entries.");

            foreach (var entry in verifiedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = VerifyIncrementalEntry(
                    target.LedgerId, entry, verified + 1, previous);
                if (failure is not null)
                    return new(false, verified,
                        new LedgerHead(target.LedgerId, verified, previous,
                            LedgerFormatV1.Version),
                        entry.Sequence, failure.Value.Failure, failure.Value.Detail);
                previous = entry.Hash;
                verified++;
            }
            options.Progress?.Report(new(verified, target.Sequence, previous));
        }

        // Reuse the established checkpoint semantics over bounded reads by
        // reading only the anchored entry when it is not the current head.
        LedgerHash? checkpointHash = null;
        if (checkpoint is not null)
        {
            if (checkpoint.Sequence == 0)
                checkpointHash = LedgerFormatV1.GenesisHash;
            else if (checkpoint.Sequence <= target.Sequence)
            {
                await foreach (var entry in ledger.ReadAsync(
                                   new LedgerReadRequest(
                                       AfterSequence: checkpoint.Sequence - 1,
                                       Limit: 1), cancellationToken).ConfigureAwait(false))
                    checkpointHash = entry.Hash;
            }
            if (checkpointHash is null)
                return new(false, verified,
                    new LedgerHead(target.LedgerId, verified, previous, LedgerFormatV1.Version),
                    checkpoint.Sequence, LedgerVerificationFailure.CheckpointSequenceMismatch,
                    "The ledger does not extend to the checkpoint sequence.");
            if (checkpointHash.Value != checkpoint.HeadHash)
                return new(false, verified,
                    new LedgerHead(target.LedgerId, verified, previous, LedgerFormatV1.Version),
                    checkpoint.Sequence, LedgerVerificationFailure.CheckpointHashMismatch,
                    "The verified chain does not contain the checkpoint head.");
        }

        var head = new LedgerHead(target.LedgerId, verified, previous, LedgerFormatV1.Version);
        if (verified != target.Sequence || previous != target.Hash)
            return new(false, verified, head, target.Sequence,
                LedgerVerificationFailure.CheckpointHashMismatch,
                "The verified entries do not match the captured ledger head.");
        return new(true, verified, head);
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
