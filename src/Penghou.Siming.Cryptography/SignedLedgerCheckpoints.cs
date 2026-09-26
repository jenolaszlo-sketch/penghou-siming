using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Penghou.Siming.Cryptography;

/// <summary>Detached signature envelope containing exact portable checkpoint bytes.</summary>
public sealed record SignedLedgerCheckpoint(
    string Algorithm,
    string KeyId,
    ReadOnlyMemory<byte> CheckpointDocument,
    ReadOnlyMemory<byte> Signature);

/// <summary>Signs detached checkpoint signatures, including OS, HSM, or remote signers.</summary>
public interface ILedgerCheckpointSigner : IDisposable
{
    /// <summary>Gets the signature algorithm identifier committed by signatures.</summary>
    string Algorithm { get; }

    /// <summary>Gets the caller-assigned key identifier committed by signatures.</summary>
    string KeyId { get; }

    /// <summary>Signs the domain-separated checkpoint input and returns the detached signature.</summary>
    byte[] Sign(ReadOnlySpan<byte> input);
}

/// <summary>Verifies detached checkpoint signatures against a trusted key.</summary>
public interface ILedgerCheckpointVerifier
{
    /// <summary>Gets the expected signature algorithm identifier.</summary>
    string Algorithm { get; }

    /// <summary>Gets the expected key identifier.</summary>
    string KeyId { get; }

    /// <summary>Gets a stable, human-checkable fingerprint of the trusted key.</summary>
    string Fingerprint { get; }

    /// <summary>Verifies a detached signature over the domain-separated checkpoint input.</summary>
    bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature);
}

/// <summary>Creates, verifies, imports, and exports signed checkpoint envelopes.</summary>
public static class SignedLedgerCheckpoints
{
    private static readonly byte[] Domain =
        "penghou-siming-signed-checkpoint-v1\0"u8.ToArray();

    /// <summary>Largest signed checkpoint envelope accepted by <see cref="Import"/>.</summary>
    public const int MaximumEnvelopeBytes = 256 * 1024;

    /// <summary>Largest UTF-8 key identifier accepted by signers and verifiers.</summary>
    public const int MaximumKeyIdUtf8Bytes = 256;

    internal static string RequireKeyId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Key ID cannot be empty.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > MaximumKeyIdUtf8Bytes)
            throw new ArgumentException(
                $"Key ID cannot exceed {MaximumKeyIdUtf8Bytes} UTF-8 bytes.", parameterName);
        return value;
    }

    /// <summary>Signs a canonical portable checkpoint document.</summary>
    public static SignedLedgerCheckpoint Sign(
        LedgerCheckpoint checkpoint,
        ILedgerCheckpointSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var document = LedgerCheckpoints.Export(checkpoint);
        return new(signer.Algorithm, signer.KeyId, document,
            signer.Sign(CreateInput(signer.KeyId, document)));
    }

    /// <summary>Verifies the signature and imports the authenticated epoch-1 checkpoint.</summary>
    public static bool Verify(
        SignedLedgerCheckpoint signed,
        ILedgerCheckpointVerifier verifier,
        out LedgerCheckpoint? checkpoint) =>
        Verify(signed, verifier, context: null, out checkpoint);

    /// <summary>
    /// Verifies the signature and imports the authenticated checkpoint. A null
    /// context accepts only epoch-1 checkpoints; a context requires epoch-2
    /// checkpoints bound to that context.
    /// </summary>
    public static bool Verify(
        SignedLedgerCheckpoint signed,
        ILedgerCheckpointVerifier verifier,
        LedgerContext? context,
        out LedgerCheckpoint? checkpoint) =>
        Verify(signed, verifier, context, key: null, out checkpoint);

    /// <summary>
    /// Verifies the signature and imports the authenticated checkpoint. A key
    /// requires a context and epoch-3 checkpoints bound to that suite, key,
    /// and context.
    /// </summary>
    public static bool Verify(
        SignedLedgerCheckpoint signed,
        ILedgerCheckpointVerifier verifier,
        LedgerContext? context,
        LedgerHmacKey? key,
        out LedgerCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(verifier);
        checkpoint = null;
        if (!signed.Algorithm.Equals(verifier.Algorithm, StringComparison.Ordinal) ||
            !signed.KeyId.Equals(verifier.KeyId, StringComparison.Ordinal) ||
            !verifier.Verify(
                CreateInput(signed.KeyId, signed.CheckpointDocument.Span),
                signed.Signature.Span))
            return false;
        try
        {
            checkpoint = LedgerCheckpoints.Import(signed.CheckpointDocument.Span, context, key);
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
        if (utf8Json.Length > MaximumEnvelopeBytes)
            throw new FormatException(
                $"The signed checkpoint envelope is {utf8Json.Length} bytes; the maximum is {MaximumEnvelopeBytes} bytes.");
        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.GetProperty("documentType").GetString() !=
                    "penghou-siming-signed-checkpoint" ||
                root.GetProperty("documentVersion").GetInt32() != 1)
                throw new FormatException("The signed checkpoint type or version is unsupported.");
            var checkpoint = root.GetProperty("checkpoint").GetBytesFromBase64();
            if (checkpoint.Length > LedgerCheckpoints.MaximumDocumentBytes)
                throw new FormatException(
                    $"The embedded checkpoint is {checkpoint.Length} bytes; the maximum is {LedgerCheckpoints.MaximumDocumentBytes} bytes.");
            var algorithm = root.GetProperty("algorithm").GetString()!;
            var keyId = RequireKeyId(root.GetProperty("keyId").GetString()!, "keyId");
            return new(
                algorithm,
                keyId,
                checkpoint,
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
public sealed class Ed25519CheckpointSigner : ILedgerCheckpointSigner
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private readonly Key key;
    /// <summary>Gets the caller-assigned key identifier committed by signatures.</summary>
    public string KeyId { get; }
    string ILedgerCheckpointSigner.Algorithm => "Ed25519";

    private Ed25519CheckpointSigner(Key key, string keyId)
    {
        this.key = key;
        KeyId = SignedLedgerCheckpoints.RequireKeyId(keyId, nameof(keyId));
    }

    /// <summary>Generates a new non-exportable Ed25519 key.</summary>
    public static Ed25519CheckpointSigner Generate(string keyId) =>
        Generate(keyId, allowPlaintextExport: false);

    /// <summary>Generates a new Ed25519 key, optionally allowing raw private-key export.</summary>
    public static Ed25519CheckpointSigner Generate(string keyId, bool allowPlaintextExport) => new(
        Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = allowPlaintextExport
                ? KeyExportPolicies.AllowPlaintextExport
                : KeyExportPolicies.None
        }), keyId);

    /// <summary>Imports a raw Ed25519 private key as a non-exportable key.</summary>
    public static Ed25519CheckpointSigner Import(
        ReadOnlySpan<byte> privateKey,
        string keyId)
    {
        if (privateKey.Length != Algorithm.PrivateKeySize)
            throw new ArgumentException(
                $"An Ed25519 private key must contain {Algorithm.PrivateKeySize} bytes.",
                nameof(privateKey));
        return new(
            Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None }),
            keyId);
    }

    /// <summary>Exports the raw private key, which requires a key generated as exportable.</summary>
    public byte[] ExportPrivateKey() => key.Export(KeyBlobFormat.RawPrivateKey);
    /// <summary>Exports the raw public verification key.</summary>
    public byte[] ExportPublicKey() => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    byte[] ILedgerCheckpointSigner.Sign(ReadOnlySpan<byte> input) => Algorithm.Sign(key, input);
    /// <inheritdoc />
    public void Dispose() => key.Dispose();
}

/// <summary>Public-key-only Ed25519 checkpoint verifier.</summary>
public sealed class Ed25519CheckpointVerifier : ILedgerCheckpointVerifier
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private readonly PublicKey key;
    /// <summary>Gets the expected key identifier.</summary>
    public string KeyId { get; }

    /// <summary>Gets the lowercase-hex SHA-256 fingerprint of the imported public key.</summary>
    public string Fingerprint { get; }
    string ILedgerCheckpointVerifier.Algorithm => "Ed25519";

    /// <summary>Imports a raw Ed25519 public key and its expected identifier.</summary>
    public Ed25519CheckpointVerifier(ReadOnlySpan<byte> publicKey, string keyId)
    {
        if (publicKey.Length != Algorithm.PublicKeySize)
            throw new ArgumentException(
                $"An Ed25519 public key must contain {Algorithm.PublicKeySize} bytes.",
                nameof(publicKey));
        key = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        KeyId = SignedLedgerCheckpoints.RequireKeyId(keyId, nameof(keyId));
        Fingerprint = Convert.ToHexString(
            SHA256.HashData(key.Export(KeyBlobFormat.RawPublicKey))).ToLowerInvariant();
    }

    bool ILedgerCheckpointVerifier.Verify(
        ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        signature.Length == Algorithm.SignatureSize &&
        Algorithm.Verify(key, input, signature);
}
