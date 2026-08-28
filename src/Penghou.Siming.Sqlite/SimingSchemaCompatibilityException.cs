namespace Penghou.Siming.Sqlite;

/// <summary>Indicates that an existing database is not a compatible Siming schema.</summary>
public sealed class SimingSchemaCompatibilityException : Exception
{
    /// <summary>Creates a compatibility exception.</summary>
    public SimingSchemaCompatibilityException(string message) : base(message)
    {
    }

    /// <summary>Creates a compatibility exception wrapping a SQLite failure.</summary>
    public SimingSchemaCompatibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
