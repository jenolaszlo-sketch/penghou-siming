using System.Text;

namespace Penghou.Siming.Tests;

public sealed class LedgerCheckpointTests
{
    [Fact]
    public async Task ExportedCheckpoint_RoundTripsAndVerifiesExactHeadAndExtension()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_234));
        var captured = await LedgerCheckpoints.CaptureAsync(ledger, clock);

        var portable = LedgerCheckpoints.Export(captured);
        var imported = LedgerCheckpoints.Import(portable);

        Assert.Equal(captured, imported);
        Assert.True((await ledger.VerifyAsync(imported)).IsValid);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "two", new byte[] { 2 }));
        Assert.True((await ledger.VerifyAsync(imported)).IsValid);
    }

    [Fact]
    public async Task EmptyLedgerCheckpoint_UsesGenesisAndRoundTrips()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());

        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        var imported = LedgerCheckpoints.Import(LedgerCheckpoints.Export(checkpoint));

        Assert.Equal(0, imported.Sequence);
        Assert.Equal(LedgerFormatV1.GenesisHash, imported.HeadHash);
        Assert.True((await ledger.VerifyAsync(imported)).IsValid);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"documentType\":\"other\",\"documentVersion\":1}")]
    [InlineData("not json")]
    public void Import_RejectsMalformedOrUnsupportedDocuments(string document)
    {
        Assert.Throws<FormatException>(() =>
            LedgerCheckpoints.Import(Encoding.UTF8.GetBytes(document)));
    }

    [Fact]
    public void Import_RejectsDocumentsBeyondTheConfiguredBound()
    {
        var oversized = new byte[LedgerCheckpoints.MaximumDocumentBytes + 1];

        var error = Assert.Throws<FormatException>(() =>
            LedgerCheckpoints.Import(oversized));

        Assert.Contains("maximum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
