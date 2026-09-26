using Penghou.Siming.Cryptography;

namespace Penghou.Siming.Tests;

public sealed class SignedLedgerCheckpointTests
{
    [Fact]
    public async Task Ed25519Signature_RoundTripsAndVerifiesWithPublicKeyOnly()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        await ledger.AppendAsync(new LedgerAppendRequest("s", "one", new byte[] { 1 }));
        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        using var signer = Ed25519CheckpointSigner.Generate("operations-2026");
        var verifier = new Ed25519CheckpointVerifier(
            signer.ExportPublicKey(), signer.KeyId);

        var signed = SignedLedgerCheckpoints.Sign(checkpoint, signer);
        var imported = SignedLedgerCheckpoints.Import(
            SignedLedgerCheckpoints.Export(signed));

        Assert.True(SignedLedgerCheckpoints.Verify(imported, verifier, out var verified));
        Assert.Equal(checkpoint, verified);
    }

    [Fact]
    public async Task MutationAndWrongKey_AreRejected()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        using var signer = Ed25519CheckpointSigner.Generate("trusted");
        using var other = Ed25519CheckpointSigner.Generate("trusted");
        var signed = SignedLedgerCheckpoints.Sign(checkpoint, signer);
        var mutatedDocument = signed.CheckpointDocument.ToArray();
        mutatedDocument[^2] ^= 1;

        Assert.False(SignedLedgerCheckpoints.Verify(
            signed with { CheckpointDocument = mutatedDocument },
            new Ed25519CheckpointVerifier(signer.ExportPublicKey(), "trusted"),
            out _));
        Assert.False(SignedLedgerCheckpoints.Verify(
            signed,
            new Ed25519CheckpointVerifier(other.ExportPublicKey(), "trusted"),
            out _));
    }

    [Fact]
    public async Task ExportedPrivateKey_CanBeImportedWithoutChangingIdentity()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        using var original = Ed25519CheckpointSigner.Generate("recoverable", allowPlaintextExport: true);
        using var imported = Ed25519CheckpointSigner.Import(
            original.ExportPrivateKey(), original.KeyId);

        var signed = SignedLedgerCheckpoints.Sign(checkpoint, imported);

        Assert.True(SignedLedgerCheckpoints.Verify(
            signed,
            new Ed25519CheckpointVerifier(original.ExportPublicKey(), original.KeyId),
            out _));
    }

    [Fact]
    public void Import_RejectsOversizedEnvelopeAndEmbeddedCheckpoint()
    {
        Assert.Throws<FormatException>(() =>
            SignedLedgerCheckpoints.Import(new byte[SignedLedgerCheckpoints.MaximumEnvelopeBytes + 1]));

        var oversizedDocument = new byte[LedgerCheckpoints.MaximumDocumentBytes + 1];
        var envelope = new SignedLedgerCheckpoint(
            "Ed25519", "k", oversizedDocument, new byte[64]);

        var error = Assert.Throws<FormatException>(() =>
            SignedLedgerCheckpoints.Import(SignedLedgerCheckpoints.Export(envelope)));

        Assert.Contains("checkpoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keys_RejectWrongSizesAndOversizedIdentifiers()
    {
        Assert.Throws<ArgumentException>(() =>
            Ed25519CheckpointSigner.Import(new byte[31], "k"));
        Assert.Throws<ArgumentException>(() =>
            new Ed25519CheckpointVerifier(new byte[31], "k"));
        Assert.Throws<ArgumentException>(() =>
            Ed25519CheckpointSigner.Generate(
                new string('a', SignedLedgerCheckpoints.MaximumKeyIdUtf8Bytes + 1)));
    }

    [Fact]
    public void GeneratedKeys_AreNonExportableByDefaultAndExportOnlyOnOptIn()
    {
        using var nonExportable = Ed25519CheckpointSigner.Generate("default");
        Assert.Throws<InvalidOperationException>(() => nonExportable.ExportPrivateKey());

        using var exportable = Ed25519CheckpointSigner.Generate("opt-in", allowPlaintextExport: true);
        Assert.Equal(32, exportable.ExportPrivateKey().Length);
        Assert.Equal(32, exportable.ExportPublicKey().Length);
    }

    [Fact]
    public async Task CustomSignerAndVerifier_PlugInThroughInterfaces()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
        using var inner = Ed25519CheckpointSigner.Generate("custom");
        var signer = new DelegatingSigner(inner);
        var verifier = new DelegatingVerifier(
            new Ed25519CheckpointVerifier(inner.ExportPublicKey(), "custom"));

        var signed = SignedLedgerCheckpoints.Sign(checkpoint, signer);

        Assert.Equal("Ed25519", signed.Algorithm);
        Assert.True(SignedLedgerCheckpoints.Verify(signed, verifier, out var verified));
        Assert.Equal(checkpoint, verified);
    }

    private sealed class DelegatingSigner(Ed25519CheckpointSigner inner) : ILedgerCheckpointSigner
    {
        public string Algorithm => ((ILedgerCheckpointSigner)inner).Algorithm;
        public string KeyId => inner.KeyId;
        public byte[] Sign(ReadOnlySpan<byte> input) => ((ILedgerCheckpointSigner)inner).Sign(input);
        public void Dispose() { }
    }

    private sealed class DelegatingVerifier(Ed25519CheckpointVerifier inner) : ILedgerCheckpointVerifier
    {
        public string Algorithm => ((ILedgerCheckpointVerifier)inner).Algorithm;
        public string KeyId => inner.KeyId;
        public string Fingerprint => inner.Fingerprint;
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
            ((ILedgerCheckpointVerifier)inner).Verify(input, signature);
    }
}
