using System.Security.Cryptography;
using System.Text;

namespace Penghou.Siming;

/// <summary>
/// Externally held HMAC key for the keyed ledger suite. Siming never persists
/// the secret: only the suite identity and key identifier travel in ledger
/// metadata, checkpoints, and diagnostics. The caller owns the secret lifetime.
/// </summary>
public sealed class LedgerHmacKey : IDisposable
{
    /// <summary>Required secret size in bytes (256 bits).</summary>
    public const int SecretSize = 32;

    /// <summary>Maximum UTF-8 key identifier size in bytes.</summary>
    public const int MaxKeyIdUtf8Bytes = 256;

    private readonly byte[] secret;

    /// <summary>Creates a keyed-suite key, copying the secret.</summary>
    public LedgerHmacKey(string keyId, ReadOnlySpan<byte> secret)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Key ID cannot be empty.", nameof(keyId));
        if (Encoding.UTF8.GetByteCount(keyId) > MaxKeyIdUtf8Bytes)
            throw new ArgumentException(
                $"Key ID cannot exceed {MaxKeyIdUtf8Bytes} UTF-8 bytes.", nameof(keyId));
        if (secret.Length != SecretSize)
            throw new ArgumentException(
                $"An HMAC suite key must contain exactly {SecretSize} bytes.", nameof(secret));
        KeyId = keyId;
        this.secret = secret.ToArray();
    }

    /// <summary>Gets the caller-assigned key identifier committed by keyed hashes.</summary>
    public string KeyId { get; }

    /// <summary>Gets the secret bytes. The caller must protect and clear its own copies.</summary>
    public ReadOnlyMemory<byte> Secret => secret;

    /// <summary>Validates the key identifier and secret size.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyId))
            throw new ArgumentException("Key ID cannot be empty.", nameof(KeyId));
        if (Encoding.UTF8.GetByteCount(KeyId) > MaxKeyIdUtf8Bytes)
            throw new ArgumentException(
                $"Key ID cannot exceed {MaxKeyIdUtf8Bytes} UTF-8 bytes.", nameof(KeyId));
        if (secret.Length != SecretSize)
            throw new ArgumentException(
                $"An HMAC suite key must contain exactly {SecretSize} bytes.", nameof(Secret));
    }

    /// <summary>Zeros the stored secret bytes.</summary>
    public void Clear() => CryptographicOperations.ZeroMemory(secret);

    /// <inheritdoc />
    public void Dispose() => Clear();
}
