namespace Penghou.Siming.Sqlite;

public sealed record SimingSqliteOptions
{
    public required string DatabasePath { get; init; }

    public bool Pooling { get; init; } = true;

    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
