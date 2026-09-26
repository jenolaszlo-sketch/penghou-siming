using System.Text;

namespace Penghou.Siming.Tests;

public sealed class LedgerContextTests
{
    private static readonly LedgerContext TestContext = new()
    {
        Application = "marang",
        Environment = "production",
        Tenant = "acme",
        Deployment = "east"
    };

    [Fact]
    public void ComputeDigest_IsStableAndFieldSensitive()
    {
        var first = TestContext.ComputeDigest();
        var repeat = new LedgerContext
        {
            Application = "marang",
            Environment = "production",
            Tenant = "acme",
            Deployment = "east"
        }.ComputeDigest();
        var changed = TestContext with { Tenant = "other" };

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, changed.ComputeDigest());
        Assert.Equal(64, first.ToString().Length);
    }

    [Fact]
    public void Validate_RejectsEmptyAndOversizedContexts()
    {
        Assert.Throws<ArgumentException>(() => new LedgerContext().Validate());
        Assert.Throws<ArgumentException>(() =>
            new LedgerContext { Application = "  " }.Validate());
        Assert.Throws<ArgumentException>(() =>
            new LedgerContext { Application = new string('a', LedgerContext.MaxFieldUtf8Bytes + 1) }
                .Validate());
    }

    [Fact]
    public void GenesisHash_BindsTheContext()
    {
        var first = LedgerFormatV2.GenesisHash(TestContext);
        var repeat = LedgerFormatV2.GenesisHash(TestContext);
        var other = LedgerFormatV2.GenesisHash(TestContext with { Deployment = "west" });

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, other);
        Assert.NotEqual(LedgerFormatV1.GenesisHash, first);
    }

    [Fact]
    public void V2_RowHash_IsStableAndBoundToContext()
    {
        var hash = LedgerFormatV2.ComputeHash(
            new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            1,
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123),
            "session-1",
            "SessionStarted",
            new SerializedLedgerPayload(
                new byte[] { 1 },
                "application/octet-stream",
                "raw",
                1),
            null,
            LedgerFormatV2.GenesisHash(TestContext),
            TestContext);

        Assert.Equal(
            "f8c7fc5beac434819cb15a90a19adfb5d622c06d4162fff17841dddc0ddc1257",
            hash.ToString());
    }

    [Fact]
    public void V2_RowHash_DiffersFromV1ForIdenticalFields()
    {
        var id = new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var payload = new SerializedLedgerPayload(
            new byte[] { 1 }, "application/octet-stream", "raw", 1);

        var v1 = LedgerFormatV1.ComputeHash(
            id, 1, time, "s", "e", payload, null, LedgerFormatV1.GenesisHash);
        var v2 = LedgerFormatV2.ComputeHash(
            id, 1, time, "s", "e", payload, null,
            LedgerFormatV2.GenesisHash(TestContext), TestContext);

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public async Task ContextBoundLedger_RoundTripsAndVerifies()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        await ledger.AppendAsync(new LedgerAppendRequest("s", "two", new byte[] { 2 }));
        var head = await ledger.GetHeadAsync();

        Assert.Equal(LedgerFormatV2.Version, head.FormatVersion);

        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        Assert.Equal(LedgerFormatV2.Version, checkpoint.FormatVersion);
        var imported = LedgerCheckpoints.Import(
            LedgerCheckpoints.Export(checkpoint), TestContext);

        Assert.Equal(checkpoint, imported);
        Assert.True((await ledger.VerifyAsync(imported)).IsValid);
    }

    [Fact]
    public async Task WrongContext_FailsVerification()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));

        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);

        var result = LedgerVerifier.Verify(
            ledger.LedgerId, entries, TestContext with { Tenant = "other" });

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task EpochMismatch_BetweenCheckpointAndLedger_ReportsUnsupportedVersion()
    {
        var id = LedgerId.New();
        await using var committed = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerId: id, ledgerContext: TestContext);
        await committed.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var boundCheckpoint = await LedgerCheckpoints.CaptureAsync(committed);

        await using var plain = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerId: id);
        await plain.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));

        Assert.Equal(
            LedgerVerificationFailure.UnsupportedVersion,
            (await plain.VerifyAsync(boundCheckpoint)).Failure);
    }

    [Fact]
    public async Task SignedContextBoundCheckpoint_VerifiesWithItsContext()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        using var signer = Penghou.Siming.Cryptography.Ed25519CheckpointSigner.Generate("epoch");
        var verifier = new Penghou.Siming.Cryptography.Ed25519CheckpointVerifier(
            signer.ExportPublicKey(), signer.KeyId);

        var signed = Penghou.Siming.Cryptography.SignedLedgerCheckpoints.Sign(checkpoint, signer);
        var imported = Penghou.Siming.Cryptography.SignedLedgerCheckpoints.Import(
            Penghou.Siming.Cryptography.SignedLedgerCheckpoints.Export(signed));

        Assert.True(Penghou.Siming.Cryptography.SignedLedgerCheckpoints.Verify(
            imported, verifier, TestContext, out var verified));
        Assert.Equal(checkpoint, verified);
        Assert.Throws<System.NotSupportedException>(() =>
            Penghou.Siming.Cryptography.SignedLedgerCheckpoints.Verify(
                imported, verifier, out _));
    }
}
