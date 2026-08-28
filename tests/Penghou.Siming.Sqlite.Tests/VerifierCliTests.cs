using System.Text.Json;
using Penghou.Siming.Cryptography;
using Penghou.Siming.Verify;

namespace Penghou.Siming.Sqlite.Tests;

public sealed class VerifierCliTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"siming-cli-{Guid.NewGuid():N}");

    [Fact]
    public async Task VerifyDatabase_WithPortableCheckpoint_ReturnsMachineReadableSuccess()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "ledger.db");
        var checkpointPath = Path.Combine(root, "checkpoint.json");
        await using (var ledger = Create(database))
        {
            await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
            await File.WriteAllBytesAsync(checkpointPath,
                LedgerCheckpoints.Export(await LedgerCheckpoints.CaptureAsync(ledger)));
        }
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [database, "--checkpoint", checkpointPath, "--page-size", "1"],
            output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(1, result.RootElement.GetProperty("verifiedEntries").GetInt64());
        Assert.Contains("Verified 1/1", error.ToString());
    }

    [Fact]
    public async Task VerifyDatabase_RejectsInvalidSignedCheckpoint()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "signed.db");
        var signedPath = Path.Combine(root, "signed.json");
        var publicKeyPath = Path.Combine(root, "public.key");
        await using var ledger = Create(database);
        await ledger.GetHeadAsync();
        using var signer = Ed25519CheckpointSigner.Generate("key-1");
        using var wrong = Ed25519CheckpointSigner.Generate("key-1");
        await File.WriteAllBytesAsync(signedPath, SignedLedgerCheckpoints.Export(
            SignedLedgerCheckpoints.Sign(await LedgerCheckpoints.CaptureAsync(ledger), signer)));
        await File.WriteAllBytesAsync(publicKeyPath, wrong.ExportPublicKey());
        using var output = new StringWriter();

        var exitCode = await Program.RunAsync(
            [database, "--signed-checkpoint", signedPath, "--public-key", publicKeyPath,
                "--key-id", "key-1"],
            output, TextWriter.Null, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("InvalidCheckpointSignature", output.ToString());
    }

    [Fact]
    public async Task VerifyDatabase_AcceptsTrustedSignedCheckpoint()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "trusted.db");
        var signedPath = Path.Combine(root, "trusted.json");
        var publicKeyPath = Path.Combine(root, "trusted.key");
        await using var ledger = Create(database);
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        using var signer = Ed25519CheckpointSigner.Generate("release-key");
        await File.WriteAllBytesAsync(signedPath, SignedLedgerCheckpoints.Export(
            SignedLedgerCheckpoints.Sign(await LedgerCheckpoints.CaptureAsync(ledger), signer)));
        await File.WriteAllBytesAsync(publicKeyPath, signer.ExportPublicKey());
        using var output = new StringWriter();

        var exitCode = await Program.RunAsync(
            [database, "--signed-checkpoint", signedPath, "--public-key", publicKeyPath,
                "--key-id", "release-key"],
            output, TextWriter.Null, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"valid\":true", output.ToString());
    }

    private static SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer> Create(
        string database) => new(
            new SimingSqliteOptions { DatabasePath = database, Pooling = false }, new());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
