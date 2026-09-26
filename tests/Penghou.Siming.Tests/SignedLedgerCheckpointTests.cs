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
        using var original = Ed25519CheckpointSigner.Generate("recoverable");
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
}
