using System.Text.Json;

namespace Penghou.Siming;

public static class LedgerCheckpoints
{
    public static async ValueTask<LedgerCheckpoint> CaptureAsync(
        IAppendOnlyLedger ledger,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var head = await ledger.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        return new LedgerCheckpoint(
            head.LedgerId,
            head.Sequence,
            head.Hash,
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            head.FormatVersion);
    }

    public static byte[] Export(LedgerCheckpoint checkpoint)
    {
        Validate(checkpoint);
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
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static LedgerCheckpoint Import(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("documentType").GetString() != "penghou-siming-checkpoint" ||
                root.GetProperty("documentVersion").GetInt32() != 1)
                throw new FormatException("The checkpoint document type or version is unsupported.");
            var ledgerId = new LedgerId(Guid.ParseExact(root.GetProperty("ledgerId").GetString()!, "D"));
            var hashText = root.GetProperty("headHash").GetString()!;
            var checkpoint = new LedgerCheckpoint(
                ledgerId,
                root.GetProperty("sequence").GetInt64(),
                new LedgerHash(Convert.FromHexString(hashText)),
                DateTimeOffset.FromUnixTimeMilliseconds(
                    root.GetProperty("createdAtUnixMilliseconds").GetInt64()),
                root.GetProperty("ledgerFormatVersion").GetInt32());
            Validate(checkpoint);
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

    private static void Validate(LedgerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.LedgerId.Value == Guid.Empty)
            throw new ArgumentException("Ledger ID cannot be empty.", nameof(checkpoint));
        if (checkpoint.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Checkpoint sequence cannot be negative.");
        if (checkpoint.FormatVersion != LedgerFormatV1.Version)
            throw new NotSupportedException(
                $"Ledger format version {checkpoint.FormatVersion} is unsupported.");
        if (checkpoint.Sequence == 0 && checkpoint.HeadHash != LedgerFormatV1.GenesisHash)
            throw new ArgumentException(
                "An empty-ledger checkpoint must contain the v1 genesis hash.",
                nameof(checkpoint));
    }
}
