using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Penghou.Siming.Tests;

public sealed class AdversarialCoverageTests
{
    [Fact]
    public void V2_RejectsMalformedUtf8Payload()
    {
        // {"a":"<invalid 0xFF>"} - the 0xFF byte is not valid UTF-8.
        var malformed = new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D };

        Assert.Throws<JsonException>(() =>
            CanonicalJsonPayloadSerializerV2.Canonicalize(malformed));
    }

    [Fact]
    public void V2_RejectsNumberTokensBeyondTheBoundedContract()
    {
        var oversized = new string('9', CanonicalJsonPayloadSerializerV2.MaximumNumberLength + 1);
        var json = Encoding.UTF8.GetBytes($"{{\"value\":{oversized}}}");

        Assert.Throws<JsonException>(() =>
            CanonicalJsonPayloadSerializerV2.Canonicalize(json));
    }

    [Fact]
    public void V2_MemoryAndSpanOverloadsAgree()
    {
        var utf8 = Encoding.UTF8.GetBytes("{\"z\":1.00,\"a\":[true,null,\"x\"]}");
        ReadOnlyMemory<byte> memory = utf8;
        ReadOnlySpan<byte> span = utf8;

        Assert.Equal(
            CanonicalJsonPayloadSerializerV2.Canonicalize(memory).ToArray(),
            CanonicalJsonPayloadSerializerV2.Canonicalize(span).ToArray());
    }

    [Fact]
    public void RowHashingCanonicalizationAndIdentity_AreCultureIndependent()
    {
        var hash = () => LedgerFormatV1.ComputeHash(
            new LedgerId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            1,
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123),
            "session-1",
            "SessionStarted",
            new(Encoding.UTF8.GetBytes("{\"amount\":1.5}"), "application/json", "raw", 1),
            "idem-1",
            LedgerFormatV1.GenesisHash);
        var canonical = () => Encoding.UTF8.GetString(
            CanonicalJsonPayloadSerializerV2.Canonicalize(
                Encoding.UTF8.GetBytes("{\"value\":1.5,\"big\":1e21,\"small\":1e-7}")).Span);
        var identity = () => CanonicalJsonPayloadSerializerV2.ComputeSha256(
            Encoding.UTF8.GetBytes("{\"value\":1.5}"));

        var expectedHash = hash();
        var expectedCanonical = canonical();
        var expectedIdentity = identity();

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            Assert.Equal(expectedHash, hash());
            Assert.Equal(expectedCanonical, canonical());
            Assert.Equal(expectedIdentity, identity());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
