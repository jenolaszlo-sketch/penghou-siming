namespace Penghou.Siming.Sqlite;

/// <summary>Configures a SQLite-backed Siming ledger.</summary>
public sealed record SimingSqliteOptions
{
    /// <summary>Gets the SQLite database file path.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Controls Microsoft.Data.Sqlite connection pooling.</summary>
    public bool Pooling { get; init; } = true;

    /// <summary>Maximum wait for a competing SQLite lock.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Controls whether the provider may create and append or only read.</summary>
    public SimingSqliteOpenMode OpenMode { get; init; } =
        SimingSqliteOpenMode.ReadWriteCreate;

    /// <summary>Gets append-input limits enforced before opening a write transaction.</summary>
    public LedgerInputLimits InputLimits { get; init; } = LedgerInputLimits.Default;

    /// <summary>
    /// Gets the ledger context for a context-bound (epoch-2) ledger, or null
    /// for an epoch-1 ledger. The context must match on every open; changing
    /// it begins a new ledger in a new database.
    /// </summary>
    public LedgerContext? LedgerContext { get; init; }
}

/// <summary>SQLite provider access intent.</summary>
public enum SimingSqliteOpenMode
{
    /// <summary>Create or open a writable ledger.</summary>
    ReadWriteCreate,
    /// <summary>Open an existing ledger without modifying it.</summary>
    ReadOnly
}
