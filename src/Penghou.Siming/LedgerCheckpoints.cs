using System.Text.Json;

namespace Penghou.Siming;

/// <summary>Captures and serializes portable versioned ledger checkpoints.</summary>
public static class LedgerCheckpoints
{
    /// <summary>Largest portable checkpoint document accepted by <see cref="Import(ReadOnlySpan{byte})"/>.</summary>
    public const int MaximumDocumentBytes = 64 * 1024;

    /// <summary>Captures the ledger's current head as a checkpoint.</summary>
    public static async ValueTask<LedgerCheckpoint> CaptureAsync(
        IAppendOnlyLedger ledger,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var head = await ledger.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LedgerCheckpoint(
            head.LedgerId,
            head.Sequence,
            head.Hash,
            DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
            head.FormatVersion);
    }

    /// <summary>Exports a deterministic portable checkpoint document.</summary>
    public static byte[] Export(LedgerCheckpoint checkpoint)
    {
        ValidateShape(checkpoint);
        if (checkpoint.FormatVersion != LedgerFormatV1.Version &&
            checkpoint.FormatVersion != LedgerFormatV2.Version &&
            checkpoint.FormatVersion != LedgerFormatV3.Version)
            throw new NotSupportedException(
                $"Ledger format version {checkpoint.FormatVersion} is unsupported.");
        if (checkpoint.FormatVersion == LedgerFormatV3.Version)
        {
            if (checkpoint.Suite != LedgerFormatV3.Suite)
                throw new ArgumentException(
                    $"A keyed-suite checkpoint must carry suite '{LedgerFormatV3.Suite}'.",
                    nameof(checkpoint));
            if (string.IsNullOrWhiteSpace(checkpoint.KeyId))
                throw new ArgumentException(
                    "A keyed-suite checkpoint must carry a key identifier.",
                    nameof(checkpoint));
        }
        else if (checkpoint.Suite is not null || checkpoint.KeyId is not null)
        {
            throw new ArgumentException(
                "Only keyed-suite checkpoints carry a suite binding.",
                nameof(checkpoint));
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", "penghou-siming-checkpoint");
            writer.WriteNumber("documentVersion", 1);
            writer.WriteString("ledgerId", checkpoint.LedgerId.Value.ToString("D"));
            writer.WriteNumber("sequence", checkpoint.Sequence);
            writer.WriteString("headHash", checkpoint.HeadHash.ToString());
            writer.WriteNumber("createdAtUnixMilliseconds", checkpoint.CreatedAt.ToUnixTimeMilliseconds());
            writer.WriteNumber("ledgerFormatVersion", checkpoint.FormatVersion);
            if (checkpoint.Suite is not null)
                writer.WriteString("suite", checkpoint.Suite);
            if (checkpoint.KeyId is not null)
                writer.WriteString("keyId", checkpoint.KeyId);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Imports and validates a portable epoch-1 checkpoint document.</summary>
    public static LedgerCheckpoint Import(ReadOnlySpan<byte> utf8Json) =>
        Import(utf8Json, context: null, key: null);

    /// <summary>
    /// Imports and validates a portable checkpoint document. A null context
    /// accepts only epoch-1 checkpoints; a context requires epoch-2
    /// checkpoints bound to that context.
    /// </summary>
    public static LedgerCheckpoint Import(ReadOnlySpan<byte> utf8Json, LedgerContext? context) =>
        Import(utf8Json, context, key: null);

    /// <summary>
    /// Imports and validates a portable checkpoint document. A key requires a
    /// context and epoch-3 checkpoints bound to that suite, key, and context.
    /// </summary>
    public static LedgerCheckpoint Import(
        ReadOnlySpan<byte> utf8Json, LedgerContext? context, LedgerHmacKey? key)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
            throw new FormatException(
                $"The checkpoint document is {utf8Json.Length} bytes; the maximum is {MaximumDocumentBytes} bytes.");
        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("documentType").GetString() != "penghou-siming-checkpoint" ||
                root.GetProperty("documentVersion").GetInt32() != 1)
                throw new FormatException("The checkpoint document type or version is unsupported.");
            var ledgerId = new LedgerId(Guid.ParseExact(root.GetProperty("ledgerId").GetString()!, "D"));
            var hashText = root.GetProperty("headHash").GetString()!;
            var suite = root.TryGetProperty("suite", out var suiteElement)
                ? suiteElement.GetString()
                : null;
            var keyId = root.TryGetProperty("keyId", out var keyIdElement)
                ? keyIdElement.GetString()
                : null;
            var checkpoint = new LedgerCheckpoint(
                ledgerId,
                root.GetProperty("sequence").GetInt64(),
                new LedgerHash(Convert.FromHexString(hashText)),
                DateTimeOffset.FromUnixTimeMilliseconds(
                    root.GetProperty("createdAtUnixMilliseconds").GetInt64()),
                root.GetProperty("ledgerFormatVersion").GetInt32(),
                suite,
                keyId);
            Validate(checkpoint, context, key);
            return checkpoint;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new FormatException("The checkpoint document is malformed.", exception);
        }
    }

    private static void ValidateShape(LedgerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.LedgerId.Value == Guid.Empty)
            throw new ArgumentException("Ledger ID cannot be empty.", nameof(checkpoint));
        if (checkpoint.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Checkpoint sequence cannot be negative.");
    }

    private static void Validate(
        LedgerCheckpoint checkpoint, LedgerContext? context, LedgerHmacKey? key)
    {
        ValidateShape(checkpoint);
        if (key is not null)
        {
            if (context is null)
                throw new ArgumentException(
                    "A keyed suite requires a ledger context.", nameof(key));
            if (checkpoint.FormatVersion != LedgerFormatV3.Version)
                throw new NotSupportedException(
                    $"Ledger format version {checkpoint.FormatVersion} is not a keyed suite epoch.");
            if (checkpoint.Suite != LedgerFormatV3.Suite)
                throw new FormatException(
                    $"The checkpoint suite '{checkpoint.Suite}' is not '{LedgerFormatV3.Suite}'.");
            if (checkpoint.KeyId != key.KeyId)
                throw new FormatException(
                    "The checkpoint key identifier does not match the verification key.");
            if (checkpoint.Sequence == 0 &&
                checkpoint.HeadHash != LedgerFormatV3.GenesisHash(context, key))
                throw new ArgumentException(
                    "An empty keyed checkpoint must contain the epoch genesis for its context and key.",
                    nameof(checkpoint));
            return;
        }
        if (checkpoint.Suite is not null || checkpoint.KeyId is not null)
            throw new FormatException(
                "Only keyed-suite checkpoints carry a suite binding.");
        if (context is null)
        {
            if (checkpoint.FormatVersion != LedgerFormatV1.Version)
                throw new NotSupportedException(
                    $"Ledger format version {checkpoint.FormatVersion} is unsupported.");
            if (checkpoint.Sequence == 0 && checkpoint.HeadHash != LedgerFormatV1.GenesisHash)
                throw new ArgumentException(
                    "An empty-ledger checkpoint must contain the v1 genesis hash.",
                    nameof(checkpoint));
            return;
        }
        if (checkpoint.FormatVersion != LedgerFormatV2.Version)
            throw new NotSupportedException(
                $"Ledger format version {checkpoint.FormatVersion} is not a context-bound epoch.");
        if (checkpoint.Sequence == 0 &&
            checkpoint.HeadHash != LedgerFormatV2.GenesisHash(context))
            throw new ArgumentException(
                "An empty context-bound checkpoint must contain the epoch genesis for its context.",
                nameof(checkpoint));
    }
}
