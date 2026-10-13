using System.Text.Json;
using Penghou.Siming;
using Penghou.Siming.Sqlite;
using Penghou.Siming.Cryptography;
using Penghou.Siming.Testing;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var database = Path.Combine(root, "ledger.db");
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
await using (var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new SimingSqliteOptions { DatabasePath = database, Pooling = false }, new()))
{
    var first = await ledger.AppendAsync(new LedgerAppendRequest("session", "observed", new byte[] { 1, 2, 3 }, IdempotencyKey: "event-1"));
    var firstHead = new LedgerHead((await ledger.GetHeadAsync()).LedgerId, first.Sequence, first.Hash, first.FormatVersion);
    var second = await ledger.AppendAsync(new LedgerAppendRequest("other", "derived", new byte[] { 4 }, IdempotencyKey: "event-2", ExpectedHead: firstHead));
    var replay = await ledger.AppendAsync(new LedgerAppendRequest("session", "observed", new byte[] { 1, 2, 3 }, IdempotencyKey: "event-1", ExpectedHead: firstHead));
    Check(replay.Sequence == first.Sequence && replay.Hash == first.Hash, "idempotency-before-stale-head");
    try { await ledger.AppendAsync(new LedgerAppendRequest("session", "observed", new byte[] { 9 }, IdempotencyKey: "event-1")); throw new InvalidOperationException("conflict accepted"); }
    catch (LedgerIdempotencyConflictException) { }
    var page = new List<LedgerEntry>();
    await foreach (var entry in ledger.ReadAsync(new LedgerReadRequest(AfterSequence: first.Sequence, Limit: 1))) page.Add(entry);
    Check(page.Count == 1 && page[0].Sequence == second.Sequence, "exclusive-page");
    var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
    var imported = LedgerCheckpoints.Import(LedgerCheckpoints.Export(checkpoint));
    Check((await ledger.VerifyAsync(imported)).IsValid, "checkpoint verification");
    using var signer = Ed25519CheckpointSigner.Generate("qualification");
    Check(SignedLedgerCheckpoints.Verify(SignedLedgerCheckpoints.Sign(checkpoint, signer),
        new Ed25519CheckpointVerifier(signer.ExportPublicKey(), signer.KeyId), out var signed) && signed == checkpoint, "signature");
    var typed = await ledger.AppendAsync(new LedgerAppendRequest<string>("typed", "value", "text", "typed-1"));
    Check(typed.Payload.Span.SequenceEqual("\"text\""u8), "typed serializer");
}
await using (var reopened = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new SimingSqliteOptions { DatabasePath = database, Pooling = false, OpenMode = SimingSqliteOpenMode.ReadOnly }, new()))
{
    Check((await reopened.GetHeadAsync()).Sequence == 3 && (await reopened.VerifyAsync()).IsValid, "reopen persisted format");
    Check((await reopened.ReadByIdempotencyKeyAsync("event-1"))!.Payload.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "exact retained bytes");
}
await using (var memory = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new()))
{
    var result = await LedgerProviderConformance.RunAsync(_ => ValueTask.FromResult<IAppendOnlyLedger>(memory));
    Check(result.Passed, "provider conformance: " + result.Failure);
}
using (var json = JsonDocument.Parse("{\"b\":1.000,\"a\":\"x\"}"))
{
    var hash = CanonicalJsonPayloadSerializerV2.ComputeSha256(json.RootElement);
    using var equivalent = JsonDocument.Parse("{\"a\":\"x\",\"b\":1}");
    Check(CanonicalJsonPayloadSerializerV2.VerifySha256(equivalent.RootElement, hash), "canonical-content path");
}
var delegationId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var fingerprint = Marang.MarangDelegationFingerprint.Compute(delegationId, "caller", "objective", "provider", "workspace");
Check(Marang.MarangDelegationFingerprint.Verify(delegationId, "caller", "objective", "provider", "workspace", fingerprint), "actual Marang fingerprint");
Check(!Marang.MarangDelegationFingerprint.Verify(delegationId, "caller", "different", "provider", "workspace", fingerprint), "Marang mismatch");
Console.WriteLine("SUPPORTED BY THIS PROBE: append/retry/conflict/page/reopen/checkpoint/signing/conformance/canonical-content");
