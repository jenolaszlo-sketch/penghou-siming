using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

/// <summary>
/// External identity bound into a context-bound ledger epoch (format v2).
/// The context is public correlation data, never a secret; changing any field
/// begins a new ledger epoch rather than mutating an existing ledger.
/// </summary>
public sealed record LedgerContext
{
    /// <summary>Maximum UTF-8 size of a single context field in bytes.</summary>
    public const int MaxFieldUtf8Bytes = 256;

    /// <summary>Gets the application identity (for example "marang").</summary>
    public string? Application { get; init; }
    /// <summary>Gets the environment identity (for example "production").</summary>
    public string? Environment { get; init; }
    /// <summary>Gets the tenant identity.</summary>
    public string? Tenant { get; init; }
    /// <summary>Gets the deployment identity (for example a region or cluster).</summary>
    public string? Deployment { get; init; }

    /// <summary>Validates that at least one field is set and every field is bounded.</summary>
    public void Validate()
    {
        Check(Application, nameof(Application));
        Check(Environment, nameof(Environment));
        Check(Tenant, nameof(Tenant));
        Check(Deployment, nameof(Deployment));
        if (Application is null && Environment is null && Tenant is null && Deployment is null)
            throw new ArgumentException("A ledger context must set at least one identity field.");
    }

    /// <summary>Computes the canonical digest committed by epoch-2 genesis and row hashes.</summary>
    public LedgerHash ComputeDigest()
    {
        Validate();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "penghou-siming-ledger-context-v1\0"u8);
        AppendField(hash, Application);
        AppendField(hash, Environment);
        AppendField(hash, Tenant);
        AppendField(hash, Deployment);
        return new LedgerHash(hash.GetHashAndReset());
    }

    private static void Check(string? value, string name)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ledger context fields cannot be empty.", name);
        if (Encoding.UTF8.GetByteCount(value) > MaxFieldUtf8Bytes)
            throw new ArgumentException(
                $"Ledger context field '{name}' exceeds {MaxFieldUtf8Bytes} UTF-8 bytes.", name);
    }

    private static void AppendField(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, [0]);
            return;
        }
        Append(hash, [1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value) =>
        hash.AppendData(value);
}
