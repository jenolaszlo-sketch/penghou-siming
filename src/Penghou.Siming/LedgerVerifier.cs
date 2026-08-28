namespace Penghou.Siming;

public static class LedgerVerifier
{
    public static LedgerVerificationResult Verify(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, LedgerCheckpoint? checkpoint = null)
    {
        if (checkpoint is not null && checkpoint.LedgerId != ledgerId)
            return Failure(ledgerId, entries, 0, null, LedgerVerificationFailure.LedgerIdentityMismatch, "The checkpoint belongs to another ledger.");

        var previous = LedgerFormatV1.GenesisHash;
        LedgerHash? checkpointHash = checkpoint?.Sequence == 0 ? previous : null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var expectedSequence = index + 1L;
            if (entry.FormatVersion != LedgerFormatV1.Version)
                return Failure(ledgerId, entries, index, entry.Sequence, LedgerVerificationFailure.UnsupportedVersion, $"Format version {entry.FormatVersion} is unsupported.");
            if (entry.Sequence != expectedSequence)
                return Failure(ledgerId, entries, index, entry.Sequence, LedgerVerificationFailure.SequenceGap, $"Expected sequence {expectedSequence}.");
            if (entry.PreviousHash != previous)
                return Failure(ledgerId, entries, index, entry.Sequence, entry.Sequence == 1 ? LedgerVerificationFailure.InvalidGenesis : LedgerVerificationFailure.PreviousHashMismatch, "The previous hash does not match the verified chain head.");
            var calculated = LedgerFormatV1.ComputeHash(ledgerId, entry.Sequence, entry.CommittedAt, entry.StreamId, entry.EventType, new SerializedLedgerPayload(entry.Payload, entry.ContentType, entry.SerializationFormat, entry.SerializationVersion), entry.IdempotencyKey, previous);
            if (calculated != entry.Hash)
                return Failure(ledgerId, entries, index, entry.Sequence, LedgerVerificationFailure.RowHashMismatch, "The stored row hash does not match its committed fields.");
            previous = entry.Hash;
            if (checkpoint?.Sequence == entry.Sequence) checkpointHash = entry.Hash;
        }

        var head = new LedgerHead(ledgerId, entries.Count, previous, LedgerFormatV1.Version);
        if (checkpoint is not null)
        {
            if (checkpoint.FormatVersion != LedgerFormatV1.Version)
                return new(false, entries.Count, head, checkpoint.Sequence, LedgerVerificationFailure.UnsupportedVersion, "The checkpoint format version is unsupported.");
            if (checkpoint.Sequence > entries.Count || checkpointHash is null)
                return new(false, entries.Count, head, checkpoint.Sequence, LedgerVerificationFailure.CheckpointSequenceMismatch, "The ledger does not extend to the checkpoint sequence.");
            if (checkpointHash.Value != checkpoint.HeadHash)
                return new(false, entries.Count, head, checkpoint.Sequence, LedgerVerificationFailure.CheckpointHashMismatch, "The verified chain does not contain the checkpoint head.");
        }
        return new(true, entries.Count, head);
    }

    private static LedgerVerificationResult Failure(LedgerId ledgerId, IReadOnlyList<LedgerEntry> entries, int verifiedEntries, long? sequence, LedgerVerificationFailure failure, string detail)
    {
        var hash = verifiedEntries == 0 ? LedgerFormatV1.GenesisHash : entries[verifiedEntries - 1].Hash;
        return new(false, verifiedEntries, new LedgerHead(ledgerId, verifiedEntries, hash, LedgerFormatV1.Version), sequence, failure, detail);
    }
}
