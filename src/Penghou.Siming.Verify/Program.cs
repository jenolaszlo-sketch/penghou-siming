using System.Text.Json;
using Penghou.Siming.Cryptography;
using Penghou.Siming.Sqlite;

namespace Penghou.Siming.Verify;

/// <summary>Command-line entry point for operational ledger verification.</summary>
public static class Program
{
    /// <summary>Runs the verifier with console input/output and Ctrl+C cancellation.</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(
                args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>Runs verification with injectable output streams for hosting and tests.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            if (args.Count == 0 || args[0] is "--help" or "-h")
            {
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return args.Count == 0 ? 2 : 0;
            }
            var database = args[0];
            if (!File.Exists(database))
                throw new FileNotFoundException(
                    "The ledger database does not exist.", database);
            string? checkpointPath = null;
            string? signedCheckpointPath = null;
            string? publicKeyPath = null;
            string? keyId = null;
            string? keyFingerprint = null;
            string? contextApplication = null;
            string? contextEnvironment = null;
            string? contextTenant = null;
            string? contextDeployment = null;
            var pageSize = 1_000;
            for (var index = 1; index < args.Count; index += 2)
            {
                if (index + 1 >= args.Count)
                    throw new ArgumentException($"Missing value for '{args[index]}'.");
                switch (args[index])
                {
                    case "--checkpoint": checkpointPath = args[index + 1]; break;
                    case "--signed-checkpoint": signedCheckpointPath = args[index + 1]; break;
                    case "--public-key": publicKeyPath = args[index + 1]; break;
                    case "--key-id": keyId = args[index + 1]; break;
                    case "--page-size": pageSize = int.Parse(args[index + 1]); break;
                    case "--context-application": contextApplication = args[index + 1]; break;
                    case "--context-environment": contextEnvironment = args[index + 1]; break;
                    case "--context-tenant": contextTenant = args[index + 1]; break;
                    case "--context-deployment": contextDeployment = args[index + 1]; break;
                    default: throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }
            if (checkpointPath is not null && signedCheckpointPath is not null)
                throw new ArgumentException(
                    "Use either --checkpoint or --signed-checkpoint, not both.");
            LedgerContext? context =
                contextApplication is null && contextEnvironment is null &&
                contextTenant is null && contextDeployment is null
                    ? null
                    : new LedgerContext
                    {
                        Application = contextApplication,
                        Environment = contextEnvironment,
                        Tenant = contextTenant,
                        Deployment = contextDeployment
                    };
            context?.Validate();

            LedgerCheckpoint? checkpoint = null;
            if (checkpointPath is not null)
                checkpoint = LedgerCheckpoints.Import(
                    await ReadBoundedAsync(checkpointPath,
                        LedgerCheckpoints.MaximumDocumentBytes, "checkpoint", cancellationToken)
                        .ConfigureAwait(false),
                    context);
            if (signedCheckpointPath is not null)
            {
                if (publicKeyPath is null || string.IsNullOrWhiteSpace(keyId))
                    throw new ArgumentException(
                        "--signed-checkpoint requires --public-key and --key-id.");
                var signed = SignedLedgerCheckpoints.Import(
                    await ReadBoundedAsync(signedCheckpointPath,
                        SignedLedgerCheckpoints.MaximumEnvelopeBytes, "signed checkpoint",
                        cancellationToken).ConfigureAwait(false));
                var verifier = new Ed25519CheckpointVerifier(
                    await ReadBoundedAsync(publicKeyPath, 4096, "public key", cancellationToken)
                        .ConfigureAwait(false),
                    keyId);
                keyFingerprint = verifier.Fingerprint;
                if (!SignedLedgerCheckpoints.Verify(signed, verifier, context, out checkpoint))
                {
                    await WriteResultAsync(output, new
                    {
                        valid = false,
                        failure = "InvalidCheckpointSignature",
                        keyFingerprint
                    }).ConfigureAwait(false);
                    return 1;
                }
            }

            await using var ledger =
                new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
                    new SimingSqliteOptions
                    {
                        DatabasePath = database,
                        OpenMode = SimingSqliteOpenMode.ReadOnly,
                        LedgerContext = context
                    }, new());
            var progress = new TextProgress(error);
            var result = await ledger.VerifyAsync(
                checkpoint, new LedgerVerificationOptions(pageSize, progress),
                cancellationToken).ConfigureAwait(false);
            await WriteResultAsync(output, new
            {
                valid = result.IsValid,
                verifiedEntries = result.VerifiedEntries,
                ledgerId = result.VerifiedHead.LedgerId.Value,
                sequence = result.VerifiedHead.Sequence,
                headHash = result.VerifiedHead.Hash.ToString(),
                failure = result.Failure?.ToString(),
                failedSequence = result.FailedSequence,
                detail = result.Detail,
                keyFingerprint
            }).ConfigureAwait(false);
            return result.IsValid ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Verification cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or OverflowException or IOException or
                UnauthorizedAccessException or SimingSchemaCompatibilityException)
        {
            await WriteErrorAsync(error, exception.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task WriteErrorAsync(TextWriter error, string detail) =>
        await error.WriteLineAsync(JsonSerializer.Serialize(
            new { valid = false, failure = "InvalidInput", detail },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
            .ConfigureAwait(false);

    private static async Task<byte[]> ReadBoundedAsync(
        string path, int maximumBytes, string kind, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > maximumBytes)
            throw new FormatException(
                $"The {kind} file is {length} bytes; the maximum is {maximumBytes} bytes.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(TextWriter output, object value) =>
        await output.WriteLineAsync(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
            .ConfigureAwait(false);

    private sealed class TextProgress(TextWriter writer) :
        IProgress<LedgerVerificationProgress>
    {
        public void Report(LedgerVerificationProgress value) => writer.WriteLine(
            $"Verified {value.VerifiedEntries}/{value.TargetEntries} entries ({value.VerifiedHash}).");
    }

    private const string Usage = """
        Usage: penghou-siming-verify <database> [options]
          --checkpoint <file>          Verify against a portable checkpoint.
          --signed-checkpoint <file>   Verify against a signed checkpoint.
          --public-key <file>          Raw Ed25519 public key for a signed checkpoint.
          --key-id <id>                Expected signed-checkpoint key identifier.
          --page-size <1..10000>       Entries read per verification page (default 1000).
          --context-application <id>   Ledger-context application identity (epoch-2 ledgers).
          --context-environment <id>   Ledger-context environment identity.
          --context-tenant <id>        Ledger-context tenant identity.
          --context-deployment <id>    Ledger-context deployment identity.
        Results and input errors are printed as machine-readable JSON.
        Signed-checkpoint runs report the verified key fingerprint.
        Exit codes: 0 valid, 1 verification failed, 2 usage/input error, 130 cancelled.
        """;
}
