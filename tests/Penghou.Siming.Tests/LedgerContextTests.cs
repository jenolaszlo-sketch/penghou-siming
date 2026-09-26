using System.Text;
using System.Text.Json;

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
        Assert.Equal(
            "e427d555bcaff8c73b11468073adc036440e918e9603eda3820c6f9fa23b7ccf",
            first.ToString());
    }

    [Fact]
    public void V2_VectorFile_RecomputesDigestGenesisAndRow()
    {
        var vector = ReadLedgerFormatVector("ledger-format-v2.json");
        var context = ContextFrom(vector);

        Assert.Equal(vector.ExpectedContextDigestHex, context.ComputeDigest().ToString());
        Assert.Equal(vector.ExpectedGenesisHex, LedgerFormatV2.GenesisHash(context).ToString());

        var hash = LedgerFormatV2.ComputeHash(
            new LedgerId(Guid.Parse(vector.LedgerId)),
            vector.Sequence,
            DateTimeOffset.FromUnixTimeMilliseconds(vector.CommittedAtUnixMilliseconds),
            vector.StreamId,
            vector.EventType,
            new SerializedLedgerPayload(
                Convert.FromBase64String(vector.PayloadBase64),
                vector.ContentType,
                vector.SerializationFormat,
                vector.SerializationVersion),
            vector.IdempotencyKey,
            LedgerFormatV2.GenesisHash(context),
            context);

        Assert.Equal(vector.ExpectedHashHex, hash.ToString());
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

    internal static LedgerContext ContextFrom(LedgerFormatVector vector) => new()
    {
        Application = vector.Context["application"],
        Environment = vector.Context["environment"],
        Tenant = vector.Context["tenant"],
        Deployment = vector.Context["deployment"]
    };

    internal static LedgerFormatVector ReadLedgerFormatVector(string fileName) =>
        JsonSerializer.Deserialize<LedgerFormatVector>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", fileName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    internal sealed record LedgerFormatVector(
        int FormatVersion,
        Dictionary<string, string?> Context,
        string? Suite,
        string? KeyId,
        string? SecretHex,
        string LedgerId,
        long Sequence,
        long CommittedAtUnixMilliseconds,
        string StreamId,
        string EventType,
        string ContentType,
        string SerializationFormat,
        int SerializationVersion,
        string PayloadBase64,
        string? IdempotencyKey,
        string ExpectedContextDigestHex,
        string ExpectedGenesisHex,
        string ExpectedHashHex);
}
