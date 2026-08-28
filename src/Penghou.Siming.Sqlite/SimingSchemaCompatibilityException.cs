namespace Penghou.Siming.Sqlite;

public sealed class SimingSchemaCompatibilityException : Exception
{
    public SimingSchemaCompatibilityException(string message) : base(message)
    {
    }

    public SimingSchemaCompatibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
