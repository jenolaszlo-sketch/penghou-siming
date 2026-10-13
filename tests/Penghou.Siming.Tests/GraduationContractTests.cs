using System.Runtime.InteropServices;
using System.Text;
using Penghou.Siming.Cryptography;

namespace Penghou.Siming.Tests;

public sealed class GraduationContractTests
{
    [Fact]
    public void HashAndKeyByteViews_CannotMutateOwnedState()
    {
        var input = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var hash = new LedgerHash(input);
        var expected = hash.ToString();
        Assert.True(MemoryMarshal.TryGetArray(hash.Bytes, out var exposed));
        exposed.Array![exposed.Offset] ^= 255;
        input[0] ^= 127;
        Assert.Equal(expected, hash.ToString());

        using var key = new LedgerHmacKey("owned", Enumerable.Repeat((byte)7, 32).ToArray());
        Assert.True(MemoryMarshal.TryGetArray(key.Secret, out exposed));
        exposed.Array![exposed.Offset] = 99;
        Assert.Equal(7, key.Secret.Span[0]);
        key.Clear();
        key.Clear();
        Assert.All(key.Secret.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => key.Validate());
        Assert.Throws<ObjectDisposedException>(() => LedgerFormatV3.GenesisHash(
            new LedgerContext { Application = "lifetime" }, key));
    }

    [Fact]
    public async Task MemoryProvider_ReturnedPayloadCannotRewriteRetainedHistory()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new());
        var input = new byte[] { 1, 2, 3 };
        var entry = await ledger.AppendAsync(new LedgerAppendRequest("s", "e", input, IdempotencyKey: "once"));
        input[0] = 8;
        Assert.True(MemoryMarshal.TryGetArray(entry.Payload, out var exposed));
        exposed.Array![exposed.Offset] = 9;
        var retained = await ledger.ReadByIdempotencyKeyAsync("once");
        Assert.Equal(new byte[] { 1, 2, 3 }, retained!.Payload.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(retained.Payload, out exposed));
        exposed.Array![exposed.Offset] = 10;
        var replay = await ledger.AppendAsync(new LedgerAppendRequest("s", "e", new byte[] { 1, 2, 3 }, IdempotencyKey: "once"));
        Assert.Equal(entry.Sequence, replay.Sequence);
        Assert.True((await ledger.VerifyAsync()).IsValid);
    }

    [Fact]
    public void CheckpointWire_PreservesFixedBytesAndRejectsAmbiguousNames()
    {
        var checkpoint = new LedgerCheckpoint(new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            1, new LedgerHash(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            DateTimeOffset.FromUnixTimeMilliseconds(1234), 1);
        const string canonical = "{\"documentType\":\"penghou-siming-checkpoint\",\"documentVersion\":1,\"ledgerId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"sequence\":1,\"headHash\":\"000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f\",\"createdAtUnixMilliseconds\":1234,\"ledgerFormatVersion\":1}";
        Assert.Equal(canonical, Encoding.UTF8.GetString(LedgerCheckpoints.Export(checkpoint)));
        Assert.Equal(checkpoint, LedgerCheckpoints.Import(Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(checkpoint, LedgerCheckpoints.Import(Encoding.UTF8.GetBytes(canonical[..^1] + ",\"future\":true}")));
        Assert.Throws<FormatException>(() => LedgerCheckpoints.Import(Encoding.UTF8.GetBytes(canonical[..^1] + ",\"sequence\":2}")));
        Assert.Throws<NotSupportedException>(() => LedgerCheckpoints.Import(Encoding.UTF8.GetBytes(canonical.Replace("\"ledgerFormatVersion\":1", "\"ledgerFormatVersion\":99"))));
        const string signed = "{\"documentType\":\"penghou-siming-signed-checkpoint\",\"documentVersion\":1,\"algorithm\":\"Ed25519\",\"keyId\":\"a\",\"checkpoint\":\"e30=\",\"signature\":\"AA==\"}";
        Assert.Throws<FormatException>(() => SignedLedgerCheckpoints.Import(Encoding.UTF8.GetBytes(signed[..^1] + ",\"keyId\":\"b\"}")));
        Assert.Throws<FormatException>(() => SignedLedgerCheckpoints.Import(Encoding.UTF8.GetBytes(signed.Replace("\"keyId\":\"a\"", "\"keyId\":null"))));
    }
}
