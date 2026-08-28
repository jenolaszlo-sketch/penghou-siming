using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Penghou.Siming.Sqlite;

/// <summary>Transactional SQLite implementation of the Siming append-only ledger.</summary>
public sealed class SqliteAppendOnlyLedger<TSerializer> :
    IAppendOnlyLedger<TSerializer>,
    IAsyncDisposable
    where TSerializer : ILedgerPayloadSerializer
{
    private readonly SimingSqliteOptions options;
    private readonly TSerializer serializer;
    private readonly TimeProvider timeProvider;
    private readonly Func<SqliteAppendFaultPoint, CancellationToken, ValueTask>?
        appendFault;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private bool initialized;
    private LedgerId ledgerId;

    /// <summary>Creates a SQLite ledger provider with explicit serializer and access options.</summary>
    public SqliteAppendOnlyLedger(
        SimingSqliteOptions options,
        TSerializer serializer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        this.options = options;
        this.serializer = serializer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal SqliteAppendOnlyLedger(
        SimingSqliteOptions options,
        TSerializer serializer,
        Func<SqliteAppendFaultPoint, CancellationToken, ValueTask> appendFault,
        TimeProvider? timeProvider = null)
        : this(options, serializer, timeProvider) =>
        this.appendFault = appendFault;

    /// <inheritdoc />
    public ValueTask<LedgerEntry> AppendAsync<T>(
        LedgerAppendRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendAsync(
            request.StreamId,
            request.EventType,
            serializer.Serialize(request.Payload),
            request.IdempotencyKey,
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LedgerEntry> AppendAsync(
        LedgerAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendAsync(
            request.StreamId,
            request.EventType,
            new SerializedLedgerPayload(
                request.Payload,
                request.ContentType,
                request.SerializationFormat,
                request.SerializationVersion),
            request.IdempotencyKey,
            cancellationToken);
    }

    private async ValueTask<LedgerEntry> AppendAsync(
        string streamId,
        string eventType,
        SerializedLedgerPayload payload,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (options.OpenMode == SimingSqliteOpenMode.ReadOnly)
            throw new InvalidOperationException(
                "Cannot append through a read-only Siming SQLite ledger.");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (idempotencyKey is not null)
        {
            var existing = await ReadByIdempotencyKeyAsync(
                connection,
                transaction,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!Matches(existing, streamId, eventType, payload))
                    throw new LedgerIdempotencyConflictException(idempotencyKey);
                return existing;
            }
        }

        var (sequence, previous) = await ReadHeadAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await InjectAsync(
            SqliteAppendFaultPoint.AfterHeadRead,
            cancellationToken).ConfigureAwait(false);
        var nextSequence = sequence + 1;
        var committedAt = timeProvider.GetUtcNow();
        var definitivePayload = payload with { Bytes = payload.Bytes.ToArray() };
        var hash = LedgerFormatV1.ComputeHash(
            ledgerId,
            nextSequence,
            committedAt,
            streamId,
            eventType,
            definitivePayload,
            idempotencyKey,
            previous);

        await InjectAsync(
            SqliteAppendFaultPoint.BeforeInsert,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ledger_entries(
                sequence, stream_id, committed_at_unix_ms, event_type,
                content_type, serialization_format, serialization_version,
                payload, idempotency_key, previous_hash, row_hash, format_version)
            VALUES (
                $sequence, $streamId, $committedAt, $eventType,
                $contentType, $serializationFormat, $serializationVersion,
                $payload, $idempotencyKey, $previousHash, $rowHash, $formatVersion);
            """;
        command.Parameters.AddWithValue("$sequence", nextSequence);
        command.Parameters.AddWithValue("$streamId", streamId);
        command.Parameters.AddWithValue(
            "$committedAt",
            committedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$contentType", definitivePayload.ContentType);
        command.Parameters.AddWithValue(
            "$serializationFormat",
            definitivePayload.SerializationFormat);
        command.Parameters.AddWithValue(
            "$serializationVersion",
            definitivePayload.SerializationVersion);
        command.Parameters.AddWithValue("$payload", definitivePayload.Bytes.ToArray());
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            (object?)idempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousHash", previous.Bytes.ToArray());
        command.Parameters.AddWithValue("$rowHash", hash.Bytes.ToArray());
        command.Parameters.AddWithValue("$formatVersion", LedgerFormatV1.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InjectAsync(
            SqliteAppendFaultPoint.AfterInsertBeforeCommit,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();

        return new LedgerEntry(
            nextSequence,
            streamId,
            committedAt,
            eventType,
            definitivePayload.ContentType,
            definitivePayload.SerializationFormat,
            definitivePayload.SerializationVersion,
            definitivePayload.Bytes,
            idempotencyKey,
            previous,
            hash,
            LedgerFormatV1.Version);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LedgerEntry> ReadAsync(
        string? streamId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long after = 0;
        while (true)
        {
            var page = new List<LedgerEntry>();
            await foreach (var entry in ReadAsync(
                               new LedgerReadRequest(
                                   streamId,
                                   after,
                                   LedgerReadRequest.MaximumLimit),
                               cancellationToken).ConfigureAwait(false))
                page.Add(entry);
            if (page.Count == 0)
                yield break;
            foreach (var entry in page)
                yield return entry;
            after = page[^1].Sequence;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LedgerEntry> ReadAsync(
        LedgerReadRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = request.StreamId is null
            ? """
              SELECT sequence, stream_id, committed_at_unix_ms, event_type,
                     content_type, serialization_format, serialization_version,
                     payload, idempotency_key, previous_hash, row_hash, format_version
              FROM ledger_entries
              WHERE sequence > $after
              ORDER BY sequence
              LIMIT $limit;
              """
            : """
              SELECT sequence, stream_id, committed_at_unix_ms, event_type,
                     content_type, serialization_format, serialization_version,
                     payload, idempotency_key, previous_hash, row_hash, format_version
              FROM ledger_entries
              WHERE sequence > $after AND stream_id = $streamId
              ORDER BY sequence
              LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$after", request.AfterSequence);
        command.Parameters.AddWithValue("$limit", request.Limit);
        if (request.StreamId is not null)
            command.Parameters.AddWithValue("$streamId", request.StreamId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadEntry(reader);
    }

    /// <inheritdoc />
    public async ValueTask<LedgerHead> GetHeadAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (sequence, hash) = await ReadHeadAsync(
            connection,
            null,
            cancellationToken).ConfigureAwait(false);
        return new(ledgerId, sequence, hash, LedgerFormatV1.Version);
    }

    /// <inheritdoc />
    public async ValueTask<LedgerVerificationResult> VerifyAsync(
        LedgerCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        return await VerifyAsync(
            checkpoint, new LedgerVerificationOptions(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Verifies one stable SQLite read snapshot with bounded progress options.</summary>
    public async ValueTask<LedgerVerificationResult> VerifyAsync(
        LedgerCheckpoint? checkpoint,
        LedgerVerificationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var (sequence, hash) = await ReadHeadAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var target = new LedgerHead(ledgerId, sequence, hash, LedgerFormatV1.Version);
        var result = await LedgerVerifier.VerifySnapshotAsync(
            target,
            (request, token) => ReadSnapshotAsync(
                connection, transaction, request, token),
            checkpoint,
            options,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return result;
    }

    private async ValueTask EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (initialized)
            return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            var fullPath = Path.GetFullPath(options.DatabasePath);
            if (options.OpenMode == SimingSqliteOpenMode.ReadWriteCreate)
                Directory.CreateDirectory(
                    Path.GetDirectoryName(fullPath) ??
                    throw new InvalidOperationException("Database path has no directory."));
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (options.OpenMode == SimingSqliteOpenMode.ReadOnly)
            {
                await InitializeReadOnlyAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                initialized = true;
                return;
            }
            await using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var transaction = connection.BeginTransaction(deferred: false);
            if (await HasExistingLedgerSchemaAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false))
            {
                await ValidateSchemaAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            await using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = SchemaSql;
            try
            {
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                throw new SimingSchemaCompatibilityException(
                    "The existing Siming SQLite schema is incompatible with format v1.",
                    exception);
            }
            await ValidateSchemaAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await using var readMetadata = connection.CreateCommand();
            readMetadata.Transaction = transaction;
            readMetadata.CommandText =
                "SELECT ledger_id, format_version FROM ledger_metadata WHERE singleton_id = 1;";
            await using var reader = await readMetadata.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ledgerId = new LedgerId(Guid.Parse(reader.GetString(0)));
                var version = reader.GetInt32(1);
                if (version != LedgerFormatV1.Version)
                    throw new SimingSchemaCompatibilityException(
                        $"Ledger format version {version} is unsupported.");
            }
            else
            {
                ledgerId = LedgerId.New();
                await reader.DisposeAsync().ConfigureAwait(false);
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO ledger_metadata(singleton_id, ledger_id, format_version) VALUES (1, $id, $version);";
                insert.Parameters.AddWithValue("$id", ledgerId.Value.ToString("D"));
                insert.Parameters.AddWithValue("$version", LedgerFormatV1.Version);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            initialized = true;
        }
        finally { initializationGate.Release(); }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Mode = options.OpenMode == SimingSqliteOpenMode.ReadOnly
                ? SqliteOpenMode.ReadOnly
                : SqliteOpenMode.ReadWriteCreate,
            Cache = options.OpenMode == SimingSqliteOpenMode.ReadOnly
                ? SqliteCacheMode.Private
                : SqliteCacheMode.Shared,
            Pooling = options.Pooling,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds))
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private async Task InitializeReadOnlyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!await HasExistingLedgerSchemaAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false))
            throw new SimingSchemaCompatibilityException(
                "The database does not contain a Siming ledger schema.");
        await ValidateSchemaAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT ledger_id, format_version FROM ledger_metadata WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new SimingSchemaCompatibilityException(
                "The database has no Siming ledger metadata row.");
        ledgerId = new LedgerId(Guid.Parse(reader.GetString(0)));
        var version = reader.GetInt32(1);
        if (version != LedgerFormatV1.Version)
            throw new SimingSchemaCompatibilityException(
                $"Ledger format version {version} is unsupported.");
        await reader.DisposeAsync().ConfigureAwait(false);
        transaction.Commit();
    }

    private static async IAsyncEnumerable<LedgerEntry> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerReadRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Validate();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, stream_id, committed_at_unix_ms, event_type,
                   content_type, serialization_format, serialization_version,
                   payload, idempotency_key, previous_hash, row_hash, format_version
            FROM ledger_entries
            WHERE sequence > $after
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", request.AfterSequence);
        command.Parameters.AddWithValue("$limit", request.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadEntry(reader);
    }

    private ValueTask InjectAsync(
        SqliteAppendFaultPoint point,
        CancellationToken cancellationToken) =>
        appendFault?.Invoke(point, cancellationToken) ?? ValueTask.CompletedTask;

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ValidateColumnsAsync(
            connection,
            transaction,
            "ledger_metadata",
            [
                new("singleton_id", "INTEGER", true, true),
                new("ledger_id", "TEXT", true, false),
                new("format_version", "INTEGER", true, false)
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateColumnsAsync(
            connection,
            transaction,
            "ledger_entries",
            [
                new("sequence", "INTEGER", true, true),
                new("stream_id", "TEXT", true, false),
                new("committed_at_unix_ms", "INTEGER", true, false),
                new("event_type", "TEXT", true, false),
                new("content_type", "TEXT", true, false),
                new("serialization_format", "TEXT", true, false),
                new("serialization_version", "INTEGER", true, false),
                new("payload", "BLOB", true, false),
                new("idempotency_key", "TEXT", false, false),
                new("previous_hash", "BLOB", true, false),
                new("row_hash", "BLOB", true, false),
                new("format_version", "INTEGER", true, false)
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateSchemaObjectsAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ledger_metadata"] =
            [
                "CHECK(singleton_id = 1)"
            ],
            ["ledger_entries"] =
            [
                "CHECK(sequence > 0)",
                "CHECK(length(stream_id) > 0)",
                "CHECK(length(previous_hash) = 32)",
                "CHECK(length(row_hash) = 32)"
            ],
            ["ix_ledger_entries_stream_sequence"] =
            [
                "CREATE INDEX",
                "ON ledger_entries(stream_id, sequence)"
            ],
            ["ux_ledger_entries_idempotency_key"] =
            [
                "CREATE UNIQUE INDEX",
                "ON ledger_entries(idempotency_key)",
                "WHERE idempotency_key IS NOT NULL"
            ],
            ["ledger_metadata_no_update"] =
            [
                "BEFORE UPDATE ON ledger_metadata",
                "RAISE(ABORT, 'Ledger metadata is immutable')"
            ],
            ["ledger_metadata_no_delete"] =
            [
                "BEFORE DELETE ON ledger_metadata",
                "RAISE(ABORT, 'Ledger metadata is immutable')"
            ],
            ["ledger_entries_no_update"] =
            [
                "BEFORE UPDATE ON ledger_entries",
                "RAISE(ABORT, 'Ledger entries are immutable')"
            ],
            ["ledger_entries_no_delete"] =
            [
                "BEFORE DELETE ON ledger_entries",
                "RAISE(ABORT, 'Ledger entries are immutable')"
            ]
        };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE name IN (
                'ledger_metadata', 'ledger_entries',
                'ix_ledger_entries_stream_sequence',
                'ux_ledger_entries_idempotency_key',
                'ledger_metadata_no_update', 'ledger_metadata_no_delete',
                'ledger_entries_no_update', 'ledger_entries_no_delete');
            """;
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            actual[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        foreach (var (name, fragments) in required)
        {
            if (!actual.TryGetValue(name, out var sql))
                throw new SimingSchemaCompatibilityException(
                    $"Required SQLite schema object '{name}' is missing.");
            var normalized = NormalizeSql(sql);
            var missing = fragments.FirstOrDefault(fragment =>
                !normalized.Contains(NormalizeSql(fragment), StringComparison.Ordinal));
            if (missing is not null)
                throw new SimingSchemaCompatibilityException(
                    $"SQLite schema object '{name}' has an incompatible definition.");
        }
    }

    private static string NormalizeSql(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private static async Task<bool> HasExistingLedgerSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name IN ('ledger_metadata', 'ledger_entries'));
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ValidateColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyCollection<RequiredColumn> required,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        var actual = new Dictionary<string, RequiredColumn>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            actual[name] = new RequiredColumn(
                name,
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(5) != 0);
        }
        var missing = required.Where(column => !actual.ContainsKey(column.Name)).ToArray();
        if (missing.Length > 0)
            throw new SimingSchemaCompatibilityException(
                $"Table '{table}' is incompatible; missing column(s): {string.Join(", ", missing.Select(column => column.Name))}.");
        foreach (var expected in required)
        {
            var found = actual[expected.Name];
            if (!StringComparer.OrdinalIgnoreCase.Equals(found.DeclaredType, expected.DeclaredType) ||
                found.NotNull != expected.NotNull ||
                found.PrimaryKey != expected.PrimaryKey)
                throw new SimingSchemaCompatibilityException(
                    $"Table '{table}' column '{expected.Name}' is incompatible; expected " +
                    $"{expected.DeclaredType} NOT NULL={expected.NotNull} PRIMARY KEY={expected.PrimaryKey}, " +
                    $"found {found.DeclaredType} NOT NULL={found.NotNull} PRIMARY KEY={found.PrimaryKey}.");
        }
    }

    private sealed record RequiredColumn(
        string Name,
        string DeclaredType,
        bool NotNull,
        bool PrimaryKey);

    private static async Task<(long Sequence, LedgerHash Hash)> ReadHeadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT sequence, row_hash FROM ledger_entries ORDER BY sequence DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), new LedgerHash((byte[])reader[1]))
            : (0, LedgerFormatV1.GenesisHash);
    }

    private static async Task<LedgerEntry?> ReadByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, stream_id, committed_at_unix_ms, event_type,
                   content_type, serialization_format, serialization_version,
                   payload, idempotency_key, previous_hash, row_hash, format_version
            FROM ledger_entries
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(reader)
            : null;
    }

    private static bool Matches(
        LedgerEntry entry,
        string streamId,
        string eventType,
        SerializedLedgerPayload payload) =>
        entry.StreamId.Equals(streamId, StringComparison.Ordinal) &&
        entry.EventType.Equals(eventType, StringComparison.Ordinal) &&
        entry.ContentType.Equals(payload.ContentType, StringComparison.Ordinal) &&
        entry.SerializationFormat.Equals(payload.SerializationFormat, StringComparison.Ordinal) &&
        entry.SerializationVersion == payload.SerializationVersion &&
        entry.Payload.Span.SequenceEqual(payload.Bytes.Span);

    private static LedgerEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6),
        (byte[])reader[7],
        reader.IsDBNull(8) ? null : reader.GetString(8),
        new LedgerHash((byte[])reader[9]),
        new LedgerHash((byte[])reader[10]),
        reader.GetInt32(11));

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS ledger_metadata(
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK(singleton_id = 1),
            ledger_id TEXT NOT NULL,
            format_version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ledger_entries(
            sequence INTEGER NOT NULL PRIMARY KEY CHECK(sequence > 0),
            stream_id TEXT NOT NULL CHECK(length(stream_id) > 0),
            committed_at_unix_ms INTEGER NOT NULL,
            event_type TEXT NOT NULL CHECK(length(event_type) > 0),
            content_type TEXT NOT NULL CHECK(length(content_type) > 0),
            serialization_format TEXT NOT NULL CHECK(length(serialization_format) > 0),
            serialization_version INTEGER NOT NULL CHECK(serialization_version > 0),
            payload BLOB NOT NULL,
            idempotency_key TEXT NULL CHECK(idempotency_key IS NULL OR length(idempotency_key) > 0),
            previous_hash BLOB NOT NULL CHECK(length(previous_hash) = 32),
            row_hash BLOB NOT NULL CHECK(length(row_hash) = 32),
            format_version INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_ledger_entries_stream_sequence
        ON ledger_entries(stream_id, sequence);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_entries_idempotency_key
        ON ledger_entries(idempotency_key)
        WHERE idempotency_key IS NOT NULL;

        CREATE TRIGGER IF NOT EXISTS ledger_metadata_no_update
        BEFORE UPDATE ON ledger_metadata
        BEGIN SELECT RAISE(ABORT, 'Ledger metadata is immutable'); END;

        CREATE TRIGGER IF NOT EXISTS ledger_metadata_no_delete
        BEFORE DELETE ON ledger_metadata
        BEGIN SELECT RAISE(ABORT, 'Ledger metadata is immutable'); END;

        CREATE TRIGGER IF NOT EXISTS ledger_entries_no_update
        BEFORE UPDATE ON ledger_entries
        BEGIN SELECT RAISE(ABORT, 'Ledger entries are immutable'); END;

        CREATE TRIGGER IF NOT EXISTS ledger_entries_no_delete
        BEFORE DELETE ON ledger_entries
        BEGIN SELECT RAISE(ABORT, 'Ledger entries are immutable'); END;
        """;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal enum SqliteAppendFaultPoint
{
    AfterHeadRead,
    BeforeInsert,
    AfterInsertBeforeCommit
}
