using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using NSec.Cryptography;

namespace Penghou.Siming.Cryptography;

/// <summary>Detached signature envelope containing exact portable checkpoint bytes.</summary>
public sealed record SignedLedgerCheckpoint(
    string Algorithm,
    string KeyId,
    ReadOnlyMemory<byte> CheckpointDocument,
    ReadOnlyMemory<byte> Signature);

/// <summary>Creates, verifies, imports, and exports signed checkpoint envelopes.</summary>
public static class SignedLedgerCheckpoints
{
    private static readonly byte[] Domain =
        "penghou-siming-signed-checkpoint-v1\0"u8.ToArray();

    /// <summary>Signs a canonical portable checkpoint document.</summary>
    public static SignedLedgerCheckpoint Sign(
        LedgerCheckpoint checkpoint,
        Ed25519CheckpointSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var document = LedgerCheckpoints.Export(checkpoint);
        return new("Ed25519", signer.KeyId, document,
            signer.Sign(CreateInput(signer.KeyId, document)));
    }

    /// <summary>Verifies the signature and imports the authenticated checkpoint.</summary>
    public static bool Verify(
        SignedLedgerCheckpoint signed,
        Ed25519CheckpointVerifier verifier,
        out LedgerCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(verifier);
        checkpoint = null;
        if (!signed.Algorithm.Equals("Ed25519", StringComparison.Ordinal) ||
            !signed.KeyId.Equals(verifier.KeyId, StringComparison.Ordinal) ||
            !verifier.Verify(
                CreateInput(signed.KeyId, signed.CheckpointDocument.Span),
                signed.Signature.Span))
            return false;
        try
        {
            checkpoint = LedgerCheckpoints.Import(signed.CheckpointDocument.Span);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Exports a deterministic signed checkpoint JSON envelope.</summary>
    public static byte[] Export(SignedLedgerCheckpoint signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", "penghou-siming-signed-checkpoint");
            writer.WriteNumber("documentVersion", 1);
            writer.WriteString("algorithm", signed.Algorithm);
            writer.WriteString("keyId", signed.KeyId);
            writer.WriteBase64String("checkpoint", signed.CheckpointDocument.Span);
            writer.WriteBase64String("signature", signed.Signature.Span);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Imports a signed checkpoint envelope without trusting its signature.</summary>
    public static SignedLedgerCheckpoint Import(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.GetProperty("documentType").GetString() !=
                    "penghou-siming-signed-checkpoint" ||
                root.GetProperty("documentVersion").GetInt32() != 1)
                throw new FormatException("The signed checkpoint type or version is unsupported.");
            return new(
                root.GetProperty("algorithm").GetString()!,
                root.GetProperty("keyId").GetString()!,
                root.GetProperty("checkpoint").GetBytesFromBase64(),
                root.GetProperty("signature").GetBytesFromBase64());
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The signed checkpoint document is malformed.", exception);
        }
    }

    private static byte[] CreateInput(string keyId, ReadOnlySpan<byte> document)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        var result = new byte[Domain.Length + sizeof(int) + keyIdBytes.Length + document.Length];
        Domain.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32BigEndian(
            result.AsSpan(Domain.Length, sizeof(int)), keyIdBytes.Length);
        keyIdBytes.CopyTo(result.AsSpan(Domain.Length + sizeof(int)));
        document.CopyTo(result.AsSpan(Domain.Length + sizeof(int) + keyIdBytes.Length));
        return result;
    }
}

/// <summary>Ed25519 private-key checkpoint signer.</summary>
public sealed class Ed25519CheckpointSigner : IDisposable
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private readonly Key key;
    /// <summary>Gets the caller-assigned key identifier committed by signatures.</summary>
    public string KeyId { get; }

    private Ed25519CheckpointSigner(Key key, string keyId)
    {
        this.key = key;
        KeyId = RequireKeyId(keyId);
    }

    /// <summary>Generates a new plaintext-exportable Ed25519 key for the preview API.</summary>
    public static Ed25519CheckpointSigner Generate(string keyId) => new(
        Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        }), keyId);

    /// <summary>Imports a raw Ed25519 private key.</summary>
    public static Ed25519CheckpointSigner Import(
        ReadOnlySpan<byte> privateKey,
        string keyId) => new(
            Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters
                {
                    ExportPolicy = KeyExportPolicies.AllowPlaintextExport
                }), keyId);

    /// <summary>Exports the raw private key. The caller must protect and clear it.</summary>
    public byte[] ExportPrivateKey() => key.Export(KeyBlobFormat.RawPrivateKey);
    /// <summary>Exports the raw public verification key.</summary>
    public byte[] ExportPublicKey() => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    internal byte[] Sign(ReadOnlySpan<byte> input) => Algorithm.Sign(key, input);
    /// <inheritdoc />
    public void Dispose() => key.Dispose();

    private static string RequireKeyId(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Key ID cannot be empty.", nameof(value))
            : value;
}

/// <summary>Public-key-only Ed25519 checkpoint verifier.</summary>
public sealed class Ed25519CheckpointVerifier
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private readonly PublicKey key;
    /// <summary>Gets the expected key identifier.</summary>
    public string KeyId { get; }

    /// <summary>Imports a raw Ed25519 public key and its expected identifier.</summary>
    public Ed25519CheckpointVerifier(ReadOnlySpan<byte> publicKey, string keyId)
    {
        key = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        KeyId = string.IsNullOrWhiteSpace(keyId)
            ? throw new ArgumentException("Key ID cannot be empty.", nameof(keyId))
            : keyId;
    }

    internal bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        signature.Length == Algorithm.SignatureSize &&
        Algorithm.Verify(key, input, signature);
}
