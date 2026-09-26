using Penghou.Siming;
using Penghou.Siming.Cryptography;
using Penghou.Siming.Sqlite;

// Runnable API usage examples. Every scenario asserts its outcome and throws
// on any surprise, so `dotnet run` doubles as a smoke test in CI.
var root = Path.Combine(Path.GetTempPath(), $"siming-samples-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await Epoch1SqliteAsync(Path.Combine(root, "epoch1.db"));
    await SignedCheckpointsAsync();
    await Epoch2ContextBoundAsync();
    await Epoch3KeyedAsync();
    Console.WriteLine("samples: all scenarios passed");
}
finally
{
    Directory.Delete(root, recursive: true);
}
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"Sample assertion failed: {message}");
}

static async Task Epoch1SqliteAsync(string database)
{
    await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(
        new SimingSqliteOptions { DatabasePath = database, Pooling = false }, new());

    var request = new LedgerAppendRequest<SessionStarted>(
        "session-7", "SessionStarted", new("marang", 3), "session-7:started");
    var first = await ledger.AppendAsync(request);
    var retry = await ledger.AppendAsync(request);
    Check(retry.Sequence == first.Sequence, "idempotent retry returns the original entry");

    await ledger.AppendAsync(new LedgerAppendRequest<SessionFinished>(
        "session-7", "SessionFinished", new("session-7", true)));
    await ledger.AppendAsync(new LedgerAppendRequest<SessionFinished>(
        "session-7", "SessionApproved", new("session-7", true)));

    var pages = 0;
    var seen = 0L;
    var after = 0L;
    while (true)
    {
        var page = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync(new LedgerReadRequest(AfterSequence: after, Limit: 2)))
            page.Add(entry);
        if (page.Count == 0)
            break;
        pages++;
        after = page[^1].Sequence;
        seen = after;
    }
    Check(pages == 2 && seen == 3, "paginated read returns all entries in order");

    var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
    var portable = LedgerCheckpoints.Export(checkpoint);
    var imported = LedgerCheckpoints.Import(portable);
    Check(imported == checkpoint, "checkpoint export/import round-trips");
    var verification = await ledger.VerifyAsync(imported);
    Check(verification.IsValid, "ledger verifies against its checkpoint");
    Console.WriteLine($"samples: epoch-1 head {verification.VerifiedHead.Hash} ({verification.VerifiedEntries} entries)");
}

static async Task SignedCheckpointsAsync()
{
    await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(new());
    await ledger.AppendAsync(new LedgerAppendRequest<SessionStarted>(
        "session-7", "SessionStarted", new("marang", 1)));
    var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);

    using var signer = Ed25519CheckpointSigner.Generate("samples", allowPlaintextExport: true);
    var signed = SignedLedgerCheckpoints.Sign(checkpoint, signer);
    var portable = SignedLedgerCheckpoints.Export(signed);
    var imported = SignedLedgerCheckpoints.Import(portable);
    var verifier = new Ed25519CheckpointVerifier(signer.ExportPublicKey(), signer.KeyId);

    Check(
        SignedLedgerCheckpoints.Verify(imported, verifier, out var verified),
        "signed checkpoint verifies with its public key");
    Check(verified == checkpoint, "verified checkpoint matches the captured head");
    Console.WriteLine($"samples: signed checkpoint key fingerprint {verifier.Fingerprint}");
}

static async Task Epoch2ContextBoundAsync()
{
    var context = new LedgerContext { Application = "marang", Environment = "samples" };
    await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(
        new(), ledgerContext: context);
    await ledger.AppendAsync(new LedgerAppendRequest<SessionStarted>(
        "session-7", "SessionStarted", new("marang", 1)));

    var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
    Check(checkpoint.FormatVersion == LedgerFormatV2.Version, "context-bound head is epoch 2");
    var imported = LedgerCheckpoints.Import(LedgerCheckpoints.Export(checkpoint), context);
    Check((await ledger.VerifyAsync(imported)).IsValid, "context-bound ledger verifies");
}

static async Task Epoch3KeyedAsync()
{
    var context = new LedgerContext { Application = "marang", Environment = "samples" };
    using var key = new LedgerHmacKey("samples-2026", Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());
    await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(
        new(), ledgerContext: context, hmacKey: key);
    await ledger.AppendAsync(new LedgerAppendRequest<SessionStarted>(
        "session-7", "SessionStarted", new("marang", 1)));

    var bound = (await LedgerCheckpoints.CaptureAsync(ledger)) with
    {
        Suite = LedgerFormatV3.Suite,
        KeyId = key.KeyId
    };
    var imported = LedgerCheckpoints.Import(LedgerCheckpoints.Export(bound), context, key);
    Check((await ledger.VerifyAsync(imported)).IsValid, "keyed ledger verifies");

    using var wrong = new LedgerHmacKey(
        key.KeyId,
        Enumerable.Range(101, 132).Take(32).Select(index => (byte)index).ToArray());
    var entries = new List<LedgerEntry>();
    await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);
    Check(
        !LedgerVerifier.Verify(ledger.LedgerId, entries, context, wrong).IsValid,
        "a wrong secret fails keyed verification");
}

sealed record SessionStarted(string Supervisor, int Attempt);
sealed record SessionFinished(string SessionId, bool Approved);
