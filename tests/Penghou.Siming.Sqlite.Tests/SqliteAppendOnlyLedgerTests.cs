using Microsoft.Data.Sqlite;
using Penghou.Siming.Testing;
using Penghou.Siming.Sqlite.TestHost;
using System.Diagnostics;

namespace Penghou.Siming.Sqlite.Tests;

public sealed class SqliteAppendOnlyLedgerTests : IDisposable
{
    [Fact]
    public async Task Append_RejectsOversizedUtf8InputWithoutPersistingAnEntry()
    {
        var options = Options("limits.db") with
        {
            InputLimits = LedgerInputLimits.Default with { MaxStreamIdUtf8Bytes = 3 }
        };
        await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(options, new());

        var error = await Assert.ThrowsAsync<LedgerInputLimitExceededException>(() =>
            ledger.AppendAsync(new LedgerAppendRequest("éé", "e", Array.Empty<byte>())).AsTask());

        Assert.Equal("streamId", error.FieldName);
        Assert.Equal(4, error.ActualBytes);
        Assert.Equal(0, (await ledger.GetHeadAsync()).Sequence);
    }
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"siming-sqlite-{Guid.NewGuid():N}");

    [Fact]
    public async Task Provider_PassesSharedConformanceSuite()
    {
        Directory.CreateDirectory(root);
        var result = await LedgerProviderConformance.RunAsync(
            _ => ValueTask.FromResult<IAppendOnlyLedger>(Create("conformance.db")));

        Assert.True(result.Passed, result.Failure);
    }

    [Fact]
    public async Task Reopen_PreservesIdentityHeadAndVerification()
    {
        Directory.CreateDirectory(root);
        await using (var first = Create("reopen.db"))
            await first.AppendAsync(new LedgerAppendRequest("s", "e", "data"u8.ToArray()));

        await using var reopened = Create("reopen.db");
        var head = await reopened.GetHeadAsync();

        Assert.Equal(1, head.Sequence);
        Assert.True((await reopened.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Triggers_RejectUpdateAndDelete()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "immutable.db");
        await using var ledger = Create("immutable.db");
        await ledger.AppendAsync(new LedgerAppendRequest("s", "e", "data"u8.ToArray()));

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE ledger_entries SET event_type = 'changed' WHERE sequence = 1;";
        var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM ledger_entries WHERE sequence = 1;";
        await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SeparateInstances_ConcurrentAppendsRemainContinuous()
    {
        Directory.CreateDirectory(root);
        await using var first = Create("concurrent.db");
        await using var second = Create("concurrent.db");
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            (index % 2 == 0 ? first : second)
                .AppendAsync(new LedgerAppendRequest<int>("s", "number", index))
                .AsTask()));

        var entries = new List<LedgerEntry>();
        await foreach (var entry in first.ReadAsync()) entries.Add(entry);
        Assert.Equal(40, entries.Count);
        Assert.Equal(Enumerable.Range(1, 40).Select(value => (long)value), entries.Select(item => item.Sequence));
        Assert.True((await first.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task SeparateInstances_AtomicallyDeduplicateSameIdempotencyKey()
    {
        Directory.CreateDirectory(root);
        await using var first = Create("deduplicate.db");
        await using var second = Create("deduplicate.db");
        var request = new LedgerAppendRequest(
            "s",
            "event",
            new byte[] { 1, 2, 3 },
            IdempotencyKey: "operation:42");

        var results = await Task.WhenAll(
            first.AppendAsync(request).AsTask(),
            second.AppendAsync(request).AsTask());

        Assert.Equal(results[0].Sequence, results[1].Sequence);
        Assert.Equal(results[0].Hash, results[1].Hash);
        Assert.Equal(1, (await first.GetHeadAsync()).Sequence);
    }

    [Theory]
    [InlineData((int)SqliteAppendFaultPoint.AfterHeadRead)]
    [InlineData((int)SqliteAppendFaultPoint.BeforeInsert)]
    [InlineData((int)SqliteAppendFaultPoint.AfterInsertBeforeCommit)]
    public async Task AppendFault_RollsBackWithoutConsumingSequence(
        int faultPointValue)
    {
        var faultPoint = (SqliteAppendFaultPoint)faultPointValue;
        Directory.CreateDirectory(root);
        var injected = false;
        await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            Options("rollback.db"),
            new CanonicalJsonPayloadSerializer(),
            (point, _) =>
            {
                if (!injected && point == faultPoint)
                {
                    injected = true;
                    throw new InvalidOperationException("Injected append failure.");
                }
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.AppendAsync(new LedgerAppendRequest("s", "failed", new byte[] { 1 })).AsTask());
        Assert.Equal(0, (await ledger.GetHeadAsync()).Sequence);

        var committed = await ledger.AppendAsync(
            new LedgerAppendRequest("s", "succeeded", new byte[] { 2 }));
        Assert.Equal(1, committed.Sequence);
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task CancellationAfterInsert_RollsBackWithoutVisibleRow()
    {
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            Options("cancel.db"),
            new CanonicalJsonPayloadSerializer(),
            (point, _) =>
            {
                if (point == SqliteAppendFaultPoint.AfterInsertBeforeCommit)
                    cancellation.Cancel();
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ledger.AppendAsync(
                new LedgerAppendRequest("s", "cancelled", new byte[] { 1 }),
                cancellation.Token).AsTask());

        Assert.Equal(0, (await ledger.GetHeadAsync()).Sequence);
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task MissingRequiredColumn_ProducesCompatibilityDiagnostic()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "bad-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ledger_metadata(
                    singleton_id INTEGER NOT NULL PRIMARY KEY,
                    ledger_id TEXT NOT NULL,
                    format_version INTEGER NOT NULL);
                CREATE TABLE ledger_entries(sequence INTEGER PRIMARY KEY, stream_id TEXT);
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var ledger = Create("bad-schema.db");

        var error = await Assert.ThrowsAsync<SimingSchemaCompatibilityException>(
            () => ledger.GetHeadAsync().AsTask());

        Assert.Contains("missing column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompatibleColumnDeclaration_ProducesCompatibilityDiagnostic()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "bad-column.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ledger_metadata(
                    singleton_id INTEGER NOT NULL PRIMARY KEY,
                    ledger_id BLOB NOT NULL,
                    format_version INTEGER NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var ledger = Create("bad-column.db");

        var error = await Assert.ThrowsAsync<SimingSchemaCompatibilityException>(
            () => ledger.GetHeadAsync().AsTask());

        Assert.Contains("ledger_id", error.Message);
        Assert.Contains("expected TEXT", error.Message);
    }

    [Fact]
    public async Task BusyTimeout_ExpiresWithoutChangingTheLedger()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "busy.db");
        await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            Options("busy.db") with { BusyTimeout = TimeSpan.FromSeconds(1) },
            new CanonicalJsonPayloadSerializer());
        await ledger.GetHeadAsync();

        await using var blocker = new SqliteConnection($"Data Source={database};Default Timeout=1");
        await blocker.OpenAsync();
        using var transaction = blocker.BeginTransaction(deferred: false);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            ledger.AppendAsync(
                new LedgerAppendRequest("s", "blocked", new byte[] { 1 })).AsTask());

        Assert.Equal(5, error.SqliteErrorCode);
        transaction.Rollback();
        Assert.Equal(0, (await ledger.GetHeadAsync()).Sequence);
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task UnsupportedMetadataVersion_ProducesCompatibilityDiagnostic()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "version.db");
        await using (var initialized = Create("version.db"))
            await initialized.GetHeadAsync();
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER ledger_metadata_no_update;
                UPDATE ledger_metadata SET format_version = 99 WHERE singleton_id = 1;
                CREATE TRIGGER ledger_metadata_no_update
                BEFORE UPDATE ON ledger_metadata
                BEGIN SELECT RAISE(ABORT, 'Ledger metadata is immutable'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var ledger = Create("version.db");

        var error = await Assert.ThrowsAsync<SimingSchemaCompatibilityException>(
            () => ledger.GetHeadAsync().AsTask());

        Assert.Contains("99", error.Message);
    }

    [Fact]
    public async Task ReadOnlyOpen_RejectsIncompleteDatabaseWithoutModifyingIt()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "incomplete.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated(value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var before = await ReadSchemaSqlAsync(database);
        await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            Options("incomplete.db") with { OpenMode = SimingSqliteOpenMode.ReadOnly },
            new());

        await Assert.ThrowsAsync<SimingSchemaCompatibilityException>(
            () => ledger.GetHeadAsync().AsTask());

        Assert.Equal(before, await ReadSchemaSqlAsync(database));
    }

    [Fact]
    public async Task ReadOnlySnapshot_RemainsStableWhileWriterAppends()
    {
        Directory.CreateDirectory(root);
        await using var writer = Create("snapshot.db");
        await writer.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        await writer.AppendAsync(new LedgerAppendRequest("s", "two", new byte[] { 2 }));
        await using var reader = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            Options("snapshot.db") with { OpenMode = SimingSqliteOpenMode.ReadOnly },
            new());
        Task<LedgerEntry>? appendTask = null;
        var progress = new CallbackProgress(value =>
        {
            if (value.VerifiedEntries != 1 || appendTask is not null)
                return;
            appendTask = writer.AppendAsync(
                new LedgerAppendRequest("s", "three", new byte[] { 3 })).AsTask();
        });

        var result = await reader.VerifyAsync(
            null, new LedgerVerificationOptions(1, progress));

        Assert.True(result.IsValid);
        Assert.Equal(2, result.VerifiedEntries);
        Assert.NotNull(appendTask);
        await appendTask;
        Assert.Equal(3, (await writer.GetHeadAsync()).Sequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.AppendAsync(new LedgerAppendRequest("s", "denied", new byte[] { 4 }))
                .AsTask());
    }

    [Fact]
    public async Task ReplacedAppendOnlyTrigger_IsRejectedAsIncompatible()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "bad-trigger.db");
        await using (var initialized = Create("bad-trigger.db"))
            await initialized.GetHeadAsync();
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER ledger_entries_no_update;
                CREATE TRIGGER ledger_entries_no_update
                BEFORE UPDATE ON ledger_entries BEGIN SELECT 1; END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var ledger = Create("bad-trigger.db");

        var error = await Assert.ThrowsAsync<SimingSchemaCompatibilityException>(
            () => ledger.GetHeadAsync().AsTask());

        Assert.Contains("ledger_entries_no_update", error.Message);
    }

    [Fact]
    public async Task KilledProcessAfterInsert_LeavesNoCommittedPartialEntry()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "crash.db");
        var signal = Path.Combine(root, "inserted.signal");
        using var process = StartChild("crash-after-insert", database, signal);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(signal) && DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Crash child exited early: {await process.StandardError.ReadToEndAsync()}");
            await Task.Delay(50);
        }
        Assert.True(File.Exists(signal), "child must reach the post-insert pre-commit boundary");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        await using var reopened = Create("crash.db");
        Assert.Equal(0, (await reopened.GetHeadAsync()).Sequence);
        var committed = await reopened.AppendAsync(
            new LedgerAppendRequest("parent", "recovered", new byte[] { 2 }));
        Assert.Equal(1, committed.Sequence);
        Assert.True((await reopened.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task MultipleProcesses_ProduceOneContinuousChain()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "multiprocess.db");
        using var first = StartChild("append", database, "20");
        using var second = StartChild("append", database, "20");
        using var third = StartChild("append", database, "20");
        await Task.WhenAll(
            first.WaitForExitAsync(),
            second.WaitForExitAsync(),
            third.WaitForExitAsync());
        await AssertSucceededAsync(first);
        await AssertSucceededAsync(second);
        await AssertSucceededAsync(third);

        await using var ledger = Create("multiprocess.db");
        var entries = new List<LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync()) entries.Add(entry);
        Assert.Equal(60, entries.Count);
        Assert.Equal(
            Enumerable.Range(1, 60).Select(value => (long)value),
            entries.Select(item => item.Sequence));
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    private SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer> Create(
        string fileName) => new(Options(fileName), new CanonicalJsonPayloadSerializer());

    private SimingSqliteOptions Options(string fileName) => new()
    {
        DatabasePath = Path.Combine(root, fileName),
        Pooling = false
    };

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(TestHostMarker).Assembly.Location);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ??
            throw new InvalidOperationException("Unable to start SQLite test child process.");
    }

    private static async Task AssertSucceededAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, $"Child exited {process.ExitCode}: {error}");
    }

    private static async Task<string[]> ReadSchemaSqlAsync(string database)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT coalesce(sql, '') FROM sqlite_master ORDER BY type, name;";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private sealed class CallbackProgress(Action<LedgerVerificationProgress> callback) :
        IProgress<LedgerVerificationProgress>
    {
        public void Report(LedgerVerificationProgress value) => callback(value);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
