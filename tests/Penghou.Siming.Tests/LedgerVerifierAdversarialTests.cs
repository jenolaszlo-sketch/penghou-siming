namespace Penghou.Siming.Tests;

public sealed class LedgerVerifierAdversarialTests
{
    [Fact]
    public async Task EveryCommittedFieldMutation_InvalidatesRowHash()
    {
        var (id, entries) = await CreateChainAsync();
        var original = entries[0];
        var mutations = new LedgerEntry[]
        {
            original with { StreamId = "changed" },
            original with { EventType = "changed" },
            original with { CommittedAt = original.CommittedAt.AddMilliseconds(1) },
            original with { ContentType = "text/plain" },
            original with { SerializationFormat = "changed" },
            original with { SerializationVersion = 2 },
            original with { Payload = "changed"u8.ToArray() },
            original with { IdempotencyKey = "changed" }
        };

        foreach (var mutation in mutations)
        {
            var candidate = entries.ToArray();
            candidate[0] = mutation;
            Assert.Equal(
                LedgerVerificationFailure.RowHashMismatch,
                LedgerVerifier.Verify(id, candidate).Failure);
        }
    }

    [Fact]
    public async Task DeletedMiddleRow_ProducesSequenceGap()
    {
        var (id, entries) = await CreateChainAsync();

        var result = LedgerVerifier.Verify(id, [entries[0], entries[2]]);

        Assert.Equal(LedgerVerificationFailure.SequenceGap, result.Failure);
        Assert.Equal(3, result.FailedSequence);
    }

    [Fact]
    public async Task ReorderedRows_ProduceSequenceGap()
    {
        var (id, entries) = await CreateChainAsync();

        var result = LedgerVerifier.Verify(id, [entries[1], entries[0], entries[2]]);

        Assert.Equal(LedgerVerificationFailure.SequenceGap, result.Failure);
    }

    [Fact]
    public async Task TruncatedLedger_FailsAgainstNewerCheckpoint()
    {
        var (id, entries) = await CreateChainAsync();
        var checkpoint = new LedgerCheckpoint(id, 3, entries[2].Hash, DateTimeOffset.UtcNow, LedgerFormatV1.Version);

        var result = LedgerVerifier.Verify(id, entries.Take(2).ToArray(), checkpoint);

        Assert.Equal(LedgerVerificationFailure.CheckpointSequenceMismatch, result.Failure);
    }

    [Fact]
    public async Task RewrittenSuffix_FailsAgainstOriginalCheckpoint()
    {
        var (id, entries) = await CreateChainAsync();
        var originalCheckpoint = new LedgerCheckpoint(id, 3, entries[2].Hash, DateTimeOffset.UtcNow, LedgerFormatV1.Version);
        var replacementPayload = new SerializedLedgerPayload("replacement"u8.ToArray(), "application/octet-stream", "raw", 1);
        var second = entries[1] with
        {
            Payload = replacementPayload.Bytes,
            Hash = LedgerFormatV1.ComputeHash(id, 2, entries[1].CommittedAt, entries[1].StreamId, entries[1].EventType, replacementPayload, entries[1].IdempotencyKey, entries[0].Hash)
        };
        var thirdPayload = new SerializedLedgerPayload(entries[2].Payload, entries[2].ContentType, entries[2].SerializationFormat, entries[2].SerializationVersion);
        var third = entries[2] with
        {
            PreviousHash = second.Hash,
            Hash = LedgerFormatV1.ComputeHash(id, 3, entries[2].CommittedAt, entries[2].StreamId, entries[2].EventType, thirdPayload, entries[2].IdempotencyKey, second.Hash)
        };

        var locallyValid = LedgerVerifier.Verify(id, [entries[0], second, third]);
        var anchored = LedgerVerifier.Verify(id, [entries[0], second, third], originalCheckpoint);

        Assert.True(locallyValid.IsValid);
        Assert.Equal(LedgerVerificationFailure.CheckpointHashMismatch, anchored.Failure);
    }

    [Fact]
    public async Task InsertedDuplicateRow_ProducesSequenceGap()
    {
        var (id, entries) = await CreateChainAsync();

        var result = LedgerVerifier.Verify(
            id, [entries[0], entries[0], entries[1], entries[2]]);

        Assert.Equal(LedgerVerificationFailure.SequenceGap, result.Failure);
        Assert.Equal(entries[0].Sequence, result.FailedSequence);
    }

    [Fact]
    public async Task UnsupportedEntryFormatVersion_ReportsUnsupportedVersion()
    {
        var (id, entries) = await CreateChainAsync();

        var result = LedgerVerifier.Verify(id, [entries[0] with { FormatVersion = 99 }]);

        Assert.Equal(LedgerVerificationFailure.UnsupportedVersion, result.Failure);
    }

    [Fact]
    public async Task UnsupportedCheckpointFormatVersion_ReportsUnsupportedVersion()
    {
        var (id, entries) = await CreateChainAsync();
        var checkpoint = new LedgerCheckpoint(
            id, 1, entries[0].Hash, DateTimeOffset.UtcNow, 99);

        var result = LedgerVerifier.Verify(id, entries, checkpoint);

        Assert.Equal(LedgerVerificationFailure.UnsupportedVersion, result.Failure);
    }

    [Fact]
    public async Task CapturedHeadMismatch_ProducesPreciseHeadDiagnostic()
    {
        var (id, entries) = await CreateChainAsync();
        var captured = new LedgerHead(id, entries.Length, entries[^1].Hash, LedgerFormatV1.Version);
        var tampered = captured with { Hash = LedgerFormatV1.GenesisHash };

        Assert.True(LedgerVerifier.Verify(id, entries, captured).IsValid);

        var result = LedgerVerifier.Verify(id, entries, tampered);

        Assert.Equal(LedgerVerificationFailure.HeadMismatch, result.Failure);
        Assert.Equal(entries.Length, result.FailedSequence);
    }

    private static async Task<(LedgerId Id, LedgerEntry[] Entries)> CreateChainAsync()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        for (var index = 1; index <= 3; index++)
            await ledger.AppendAsync(new LedgerAppendRequest("s", $"e-{index}", new byte[] { (byte)index }));
        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);
        return (ledger.LedgerId, entries.ToArray());
    }
}
