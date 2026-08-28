namespace Penghou.Siming.Tests;

public sealed class InMemoryAppendOnlyLedgerTests
{
    [Fact]
    public async Task TypedAndRawAppends_FormOneVerifiableGlobalChain()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new CanonicalJsonPayloadSerializer(),
            ledgerId: new LedgerId(Guid.Parse("018f0000-0000-7000-8000-000000000001")));

        var first = await ledger.AppendAsync(new LedgerAppendRequest<object>("session-a", "Started", new { Name = "A" }));
        var second = await ledger.AppendAsync(new LedgerAppendRequest("session-b", "Bytes", new byte[] { 1, 2, 3 }));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.True((await ledger.VerifyAsync()).IsValid);
        Assert.Single(await ReadAllAsync(ledger, "session-a"));
    }

    [Fact]
    public async Task EarlierCheckpoint_VerifiesAValidExtension()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        var first = await ledger.AppendAsync(new LedgerAppendRequest("s", "one", "1"u8.ToArray()));
        var checkpoint = new LedgerCheckpoint(ledger.LedgerId, 1, first.Hash, DateTimeOffset.UtcNow, LedgerFormatV1.Version);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "two", "2"u8.ToArray()));

        Assert.True((await ledger.VerifyAsync(checkpoint)).IsValid);
        var foreign = checkpoint with { LedgerId = LedgerId.New() };
        Assert.Equal(LedgerVerificationFailure.LedgerIdentityMismatch, (await ledger.VerifyAsync(foreign)).Failure);
    }

    [Fact]
    public async Task ConcurrentAppends_ProduceContinuousValidSequence()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            ledger.AppendAsync(new LedgerAppendRequest<int>("s", "number", index)).AsTask()));

        var entries = await ReadAllAsync(ledger);
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), entries.Select(item => item.Sequence));
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    [Fact]
    public void Verifier_DetectsPayloadMutation()
    {
        var ledgerId = LedgerId.New();
        var payload = new SerializedLedgerPayload("original"u8.ToArray(), "text/plain", "raw", 1);
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var hash = LedgerFormatV1.ComputeHash(ledgerId, 1, time, "s", "e", payload, null, LedgerFormatV1.GenesisHash);
        var entry = new LedgerEntry(1, "s", time, "e", payload.ContentType, payload.SerializationFormat, payload.SerializationVersion, "changed"u8.ToArray(), null, LedgerFormatV1.GenesisHash, hash, LedgerFormatV1.Version);

        var result = LedgerVerifier.Verify(ledgerId, [entry]);

        Assert.False(result.IsValid);
        Assert.Equal(LedgerVerificationFailure.RowHashMismatch, result.Failure);
        Assert.Equal(1, result.FailedSequence);
    }

    private static async Task<List<LedgerEntry>> ReadAllAsync(IAppendOnlyLedger ledger, string? streamId = null)
    {
        var result = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync(streamId)) result.Add(entry);
        return result;
    }
}
