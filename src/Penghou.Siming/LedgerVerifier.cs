namespace Penghou.Siming;

/// <summary>Verifies ledger hash-chain and checkpoint integrity.</summary>
public static partial class LedgerVerifier
{
    /// <summary>Verifies an in-memory complete epoch-1 ledger representation.</summary>
    public static LedgerVerificationResult Verify(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, LedgerCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var state = new State(ledgerId, checkpoint, null);
        var start = state.Start(null);
        if (start is not null) return start;
        foreach (var entry in entries)
        {
            var failure = state.Accept(entry);
            if (failure is not null) return failure;
        }
        return state.Complete(null);
    }

    /// <summary>Verifies an in-memory snapshot against an independently captured epoch-1 head and checkpoint.</summary>
    public static LedgerVerificationResult Verify(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, LedgerHead target, LedgerCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(target);
        var state = new State(ledgerId, checkpoint, null);
        var start = state.Start(target);
        if (start is not null) return start;
        foreach (var entry in entries)
        {
            var failure = state.Accept(entry);
            if (failure is not null) return failure;
        }
        return state.Complete(target);
    }

    /// <summary>Verifies an in-memory complete context-bound ledger representation.</summary>
    public static LedgerVerificationResult Verify(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, LedgerContext context, LedgerCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        var state = new State(ledgerId, checkpoint, null, context);
        var start = state.Start(null);
        if (start is not null) return start;
        foreach (var entry in entries)
        {
            var failure = state.Accept(entry);
            if (failure is not null) return failure;
        }
        return state.Complete(null);
    }

    /// <summary>Verifies an in-memory context-bound snapshot against an independently captured head and checkpoint.</summary>
    public static LedgerVerificationResult Verify(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, LedgerHead target, LedgerContext context, LedgerCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        var state = new State(ledgerId, checkpoint, null, context);
        var start = state.Start(target);
        if (start is not null) return start;
        foreach (var entry in entries)
        {
            var failure = state.Accept(entry);
            if (failure is not null) return failure;
        }
        return state.Complete(target);
    }

    /// <summary>
    /// Incremental verification state machine shared by synchronous and
    /// asynchronous verification, with snapshot and head diagnostics.
    /// A null context verifies the epoch-1 chain; a context verifies the
    /// context-bound epoch-2 chain. Epochs never mix within one verification.
    /// </summary>
    private sealed class State
    {
        private readonly LedgerId ledgerId;
        private readonly LedgerCheckpoint? checkpoint;
        private readonly IProgress<LedgerVerificationProgress>? progress;
        private readonly LedgerContext? context;
        private readonly int formatVersion;
        private readonly LedgerHash genesis;
        private LedgerHash previous;
        private LedgerHash? checkpointHash;
        private long verified;

        public State(
            LedgerId ledgerId,
            LedgerCheckpoint? checkpoint,
            IProgress<LedgerVerificationProgress>? progress,
            LedgerContext? context = null)
        {
            this.ledgerId = ledgerId;
            this.checkpoint = checkpoint;
            this.progress = progress;
            this.context = context;
            formatVersion = context is null ? LedgerFormatV1.Version : LedgerFormatV2.Version;
            genesis = context is null
                ? LedgerFormatV1.GenesisHash
                : LedgerFormatV2.GenesisHash(context);
            previous = genesis;
            checkpointHash = checkpoint?.Sequence == 0 ? genesis : null;
        }

        public long Verified => verified;

        private LedgerHead CurrentHead =>
            new(ledgerId, verified, previous, formatVersion);

        /// <summary>Validates the checkpoint and captured head before reading entries.</summary>
        public LedgerVerificationResult? Start(LedgerHead? target)
        {
            if (checkpoint is not null && checkpoint.LedgerId != ledgerId)
                return StartFailure(checkpoint.Sequence, LedgerVerificationFailure.LedgerIdentityMismatch,
                    "The checkpoint belongs to another ledger.");
            if (checkpoint is not null && checkpoint.FormatVersion != formatVersion)
                return StartFailure(checkpoint.Sequence, LedgerVerificationFailure.UnsupportedVersion,
                    $"The checkpoint targets format version {checkpoint.FormatVersion}; this verification expects epoch {formatVersion}.");
            if (target is not null && checkpoint is not null && checkpoint.Sequence > target.Sequence)
                return StartFailure(checkpoint.Sequence, LedgerVerificationFailure.CheckpointSequenceMismatch,
                    "The ledger does not extend to the checkpoint sequence.");
            return null;
        }

        /// <summary>Reports a page boundary to the configured progress sink.</summary>
        public void ReportProgress(long targetSequence) =>
            progress?.Report(new(verified, targetSequence, previous));

        /// <summary>Verifies and advances over one entry, or returns the failure.</summary>
        public LedgerVerificationResult? Accept(LedgerEntry entry)
        {
            var failure = VerifyIncrementalEntry(ledgerId, entry, verified + 1, previous, context);
            if (failure is not null)
                return new(false, verified, CurrentHead, entry.Sequence,
                    failure.Value.Failure, failure.Value.Detail);
            previous = entry.Hash;
            verified++;
            if (checkpoint?.Sequence == entry.Sequence)
                checkpointHash = entry.Hash;
            return null;
        }

        /// <summary>Reports that the captured head cannot be reached from persisted entries.</summary>
        public LedgerVerificationResult HeadGap() =>
            new(false, verified, CurrentHead, verified + 1,
                LedgerVerificationFailure.SequenceGap,
                "The captured ledger head cannot be reached from persisted entries.");

        /// <summary>Finalizes checkpoint and head/snapshot diagnostics.</summary>
        public LedgerVerificationResult Complete(LedgerHead? target)
        {
            if (checkpoint is not null)
            {
                if (checkpointHash is null)
                    return new(false, verified, CurrentHead, checkpoint.Sequence,
                        LedgerVerificationFailure.CheckpointSequenceMismatch,
                        "The ledger does not extend to the checkpoint sequence.");
                if (checkpointHash.Value != checkpoint.HeadHash)
                    return new(false, verified, CurrentHead, checkpoint.Sequence,
                        LedgerVerificationFailure.CheckpointHashMismatch,
                        "The verified chain does not contain the checkpoint head.");
            }
            var head = new LedgerHead(ledgerId, verified, previous, formatVersion);
            if (target is not null && (target.FormatVersion != formatVersion || verified != target.Sequence || previous != target.Hash))
                return new(false, verified, head, target.Sequence,
                    LedgerVerificationFailure.HeadMismatch,
                    target.FormatVersion != formatVersion
                        ? $"The captured head targets format version {target.FormatVersion}; this verification expects epoch {formatVersion}."
                        : "The verified entries do not match the captured ledger head.");
            return new(true, verified, head);
        }

        private LedgerVerificationResult StartFailure(
            long sequence, LedgerVerificationFailure failure, string detail) =>
            new(false, 0,
                new LedgerHead(ledgerId, 0, genesis, formatVersion),
                sequence, failure, detail);
    }
}
