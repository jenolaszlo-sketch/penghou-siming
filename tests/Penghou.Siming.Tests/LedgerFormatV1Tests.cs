using System.Text;

namespace Penghou.Siming.Tests;

public sealed class LedgerFormatV1Tests
{
    [Fact]
    public void GoldenVector_IsStableAndIndependentlyReproducible()
    {
        var hash = LedgerFormatV1.ComputeHash(
            new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            1,
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123),
            "session-1",
            "SessionStarted",
            new SerializedLedgerPayload(
                Encoding.UTF8.GetBytes("{\"name\":\"demo\"}"),
                "application/json",
                "penghou-canonical-json",
                1),
            null,
            LedgerFormatV1.GenesisHash);

        Assert.Equal(
            "f31470fc756cc7e09a8f87eeb93643f4586f572487d18524adafe88b6318c9e9",
            hash.ToString());
    }

    [Fact]
    public void Hash_CommitsSerializerIdentityAndExactPayloadBytes()
    {
        var id = new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var first = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new("x"u8.ToArray(), "text/plain", "raw", 1), null, LedgerFormatV1.GenesisHash);
        var changedBytes = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new("y"u8.ToArray(), "text/plain", "raw", 1), null, LedgerFormatV1.GenesisHash);
        var changedFormat = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new("x"u8.ToArray(), "text/plain", "other", 1), null, LedgerFormatV1.GenesisHash);

        Assert.NotEqual(first, changedBytes);
        Assert.NotEqual(first, changedFormat);
    }

    [Fact]
    public void LargePayload_HashesDeterministicallyWithoutConcatenation()
    {
        var id = new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var payload = new byte[(1 << 20) + 7];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
        var changed = payload.ToArray();
        changed[^1] ^= 1;

        var first = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new(payload, "application/octet-stream", "raw", 1), null, LedgerFormatV1.GenesisHash);
        var repeat = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new(payload, "application/octet-stream", "raw", 1), null, LedgerFormatV1.GenesisHash);
        var different = LedgerFormatV1.ComputeHash(id, 1, time, "s", "e", new(changed, "application/octet-stream", "raw", 1), null, LedgerFormatV1.GenesisHash);

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, different);
    }
}
