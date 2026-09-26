namespace Penghou.Siming.Tests;

public sealed class LedgerKeyedSuiteTests
{
    private static readonly LedgerContext TestContext = new()
    {
        Application = "marang",
        Environment = "production"
    };

    private static LedgerHmacKey TestKey() => new(
        "ops-2026",
        Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());

    [Fact]
    public void Key_RejectsBadIdentifiersAndSecrets()
    {
        Assert.Throws<ArgumentException>(() =>
            new LedgerHmacKey("", new byte[32]));
        Assert.Throws<ArgumentException>(() =>
            new LedgerHmacKey(new string('k', LedgerHmacKey.MaxKeyIdUtf8Bytes + 1), new byte[32]));
        Assert.Throws<ArgumentException>(() =>
            new LedgerHmacKey("ops", new byte[31]));
        Assert.Throws<ArgumentException>(() =>
            new LedgerHmacKey("ops", new byte[33]));
    }

    [Fact]
    public void Key_Clear_ZerosTheSecret()
    {
        using var _ = TestKey();
        var key = TestKey();
        key.Clear();

        Assert.All(key.Secret.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void GenesisHash_BindsContextSuiteKeyAndSecret()
    {
        using var key = TestKey();
        var first = LedgerFormatV3.GenesisHash(TestContext, key);
        var repeat = LedgerFormatV3.GenesisHash(TestContext, key);

        using var rotated = new LedgerHmacKey("ops-2027", key.Secret.ToArray());
        using var wrongSecret = new LedgerHmacKey(
            "ops-2026",
            Enumerable.Range(2, 33).Take(32).Select(index => (byte)index).ToArray());

        Assert.Equal(first, repeat);
        Assert.Equal(
            "b140b2fcf8db203cc6bf93a1a1c3277d245b1e1ffb3e17a7b55696703d1dd0ef",
            first.ToString());
        Assert.NotEqual(first, LedgerFormatV3.GenesisHash(TestContext, rotated));
        Assert.NotEqual(first, LedgerFormatV3.GenesisHash(TestContext, wrongSecret));
        Assert.NotEqual(first, LedgerFormatV3.GenesisHash(
            TestContext with { Environment = "staging" }, key));
        Assert.NotEqual(LedgerFormatV1.GenesisHash, first);
        Assert.NotEqual(LedgerFormatV2.GenesisHash(TestContext), first);
    }

    [Fact]
    public void V3_VectorFile_RecomputesDigestGenesisAndRow()
    {
        var vector = LedgerContextTests.ReadLedgerFormatVector("ledger-format-v3.json");
        var context = LedgerContextTests.ContextFrom(vector);
        using var key = new LedgerHmacKey(vector.KeyId!, Convert.FromHexString(vector.SecretHex!));

        Assert.Equal(LedgerFormatV3.Suite, vector.Suite);
        Assert.Equal(vector.ExpectedContextDigestHex, context.ComputeDigest().ToString());
        Assert.Equal(
            vector.ExpectedGenesisHex,
            LedgerFormatV3.GenesisHash(context, key).ToString());

        var hash = LedgerFormatV3.ComputeHash(
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
            LedgerFormatV3.GenesisHash(context, key),
            context,
            key);

        Assert.Equal(vector.ExpectedHashHex, hash.ToString());
    }

    [Fact]
    public async Task KeyedLedger_RoundTripsCheckpointsAndVerification()
    {
        using var key = TestKey();
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext, hmacKey: key);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        await ledger.AppendAsync(new LedgerAppendRequest("s", "two", new byte[] { 2 }));
        var head = await ledger.GetHeadAsync();

        Assert.Equal(LedgerFormatV3.Version, head.FormatVersion);

        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        var bound = checkpoint with
        {
            Suite = LedgerFormatV3.Suite,
            KeyId = key.KeyId
        };
        var imported = LedgerCheckpoints.Import(
            LedgerCheckpoints.Export(bound), TestContext, key);

        Assert.Equal(bound, imported);
        Assert.True((await ledger.VerifyAsync(imported)).IsValid);
    }

    [Fact]
    public async Task WrongSecret_FailsVerification()
    {
        using var key = TestKey();
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext, hmacKey: key);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));

        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);

        using var wrong = new LedgerHmacKey(
            key.KeyId,
            Enumerable.Range(101, 132).Take(32).Select(index => (byte)index).ToArray());
        var result = LedgerVerifier.Verify(ledger.LedgerId, entries, TestContext, wrong);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RotatedKey_DoesNotVerifyOldHistory()
    {
        using var key = TestKey();
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext, hmacKey: key);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);

        using var rotated = new LedgerHmacKey("ops-2027", key.Secret.ToArray());
        var result = LedgerVerifier.Verify(ledger.LedgerId, entries, TestContext, rotated);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task CheckpointKeyConfusion_ReportsIdentityMismatch()
    {
        using var key = TestKey();
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new(), ledgerContext: TestContext, hmacKey: key);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);
        var checkpoint = (await LedgerCheckpoints.CaptureAsync(ledger)) with
        {
            Suite = LedgerFormatV3.Suite,
            KeyId = "someone-else"
        };

        var result = LedgerVerifier.Verify(
            ledger.LedgerId, entries, TestContext, key, checkpoint);

        Assert.Equal(LedgerVerificationFailure.LedgerIdentityMismatch, result.Failure);
    }

    [Fact]
    public void Key_RequiresContext()
    {
        using var key = TestKey();

        Assert.Throws<ArgumentException>(() =>
            new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
                new(), ledgerContext: null, hmacKey: key));
    }
}
