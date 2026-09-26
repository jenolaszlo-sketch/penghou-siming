using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Penghou.Siming.Tests;

public sealed partial class CanonicalJsonPayloadSerializerTests
{
    [Fact]
    public void Canonicalize_IgnoresObjectOrderWhitespaceAndNumberSpelling()
    {
        using var left = JsonDocument.Parse("""{ "z": 1.0, "a": [true, null, "x"] }""");
        using var right = JsonDocument.Parse("""{"a":[true,null,"x"],"z":1.00}""");

        var leftBytes = CanonicalJsonPayloadSerializer.Canonicalize(left.RootElement);
        var rightBytes = CanonicalJsonPayloadSerializer.Canonicalize(right.RootElement);

        Assert.Equal(leftBytes.ToArray(), rightBytes.ToArray());
        Assert.Equal("{\"a\":[true,null,\"x\"],\"z\":1}", Encoding.UTF8.GetString(leftBytes.Span));
    }

    [Fact]
    public void Serialize_DescribesTheCommittedFormat()
    {
        var result = new CanonicalJsonPayloadSerializer().Serialize(new { Value = 42 });

        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(CanonicalJsonPayloadSerializer.Format, result.SerializationFormat);
        Assert.Equal(CanonicalJsonPayloadSerializer.Version, result.SerializationVersion);
        Assert.Equal("{\"value\":42}", Encoding.UTF8.GetString(result.Bytes.Span));
    }

    [Fact]
    public void V2_Serialize_DescribesTheCommittedFormat()
    {
        var result = new CanonicalJsonPayloadSerializerV2().Serialize(new { Value = 42 });

        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(CanonicalJsonPayloadSerializerV2.Format, result.SerializationFormat);
        Assert.Equal(CanonicalJsonPayloadSerializerV2.Version, result.SerializationVersion);
        Assert.Equal("{\"value\":42}", Encoding.UTF8.GetString(result.Bytes.Span));
    }

    [Fact]
    public void Canonicalize_MatchesPortableGuyabanoV2Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "guyabano-canonical-json-v2.json");
        var vectors = JsonSerializer.Deserialize<CanonicalVector[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (var vector in vectors)
        {
            using var input = JsonDocument.Parse(vector.InputJson);
            var bytes = CanonicalJsonPayloadSerializer.Canonicalize(input.RootElement);
            Assert.Equal(vector.CanonicalJson, Encoding.UTF8.GetString(bytes.Span));
            Assert.Equal(vector.Sha256, Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
        }
    }

    [Fact]
    public void V2_RejectsDuplicatePropertiesRecursively()
    {
        using var input = JsonDocument.Parse("{\"outer\":{\"name\":1,\"name\":2}}");

        Assert.Throws<JsonException>(() => CanonicalJsonPayloadSerializerV2.Canonicalize(input.RootElement));
    }

    [Fact]
    public void V2_CanonicalizesNumbersWithoutPrecisionLoss()
    {
        using var input = JsonDocument.Parse("""
            {"huge":1234567890123456789012345678901234567890,"negative":-123456789012345678901234567890,"fraction":-0.123456789012345678901234567890,"precision":0.123456789012345678901234567890,"one":1e3,"alsoOne":1000.00,"negativeZero":-0.0,"positiveZero":0e+8}
            """);

        var json = Encoding.UTF8.GetString(CanonicalJsonPayloadSerializerV2.Canonicalize(input.RootElement).Span);

        Assert.Equal(
            "{\"alsoOne\":1000,\"fraction\":-0.12345678901234567890123456789,\"huge\":1.23456789012345678901234567890123456789E+39,\"negative\":-1.2345678901234567890123456789E+29,\"negativeZero\":0,\"one\":1000,\"positiveZero\":0,\"precision\":0.12345678901234567890123456789}",
            json);
    }

    [Fact]
    public void V2_RejectsUnboundedExponentTokens()
    {
        using var input = JsonDocument.Parse("{\"value\":1e+" + new string('9', CanonicalJsonPayloadSerializerV2.MaximumExponentLength + 1) + "}");

        // The token is valid JSON, but the v2 contract keeps exponent work bounded.
        Assert.Throws<JsonException>(() => CanonicalJsonPayloadSerializerV2.Canonicalize(input.RootElement));
    }

    [Fact]
    public void V2_EquivalentExponentSpellingsHaveOneRepresentation()
    {
        using var left = JsonDocument.Parse("{\"value\":1e-7}");
        using var right = JsonDocument.Parse("{\"value\":0.00000010e0}");

        Assert.Equal(
            CanonicalJsonPayloadSerializerV2.Canonicalize(left.RootElement).ToArray(),
            CanonicalJsonPayloadSerializerV2.Canonicalize(right.RootElement).ToArray());
        Assert.Equal("{\"value\":1E-07}", Encoding.UTF8.GetString(CanonicalJsonPayloadSerializerV2.Canonicalize(left.RootElement).Span));
    }

    [Fact]
    public void V2_CanonicalizesPersistedUtf8JsonWithTheSameContract()
    {
        var persisted = Encoding.UTF8.GetBytes(" { \"z\": 1.00, \"a\": [true, null, \"<tag>\\u2028\"] } ");

        var bytes = CanonicalJsonPayloadSerializerV2.Canonicalize(persisted);

        Assert.Equal("{\"a\":[true,null,\"\\u003Ctag\\u003E\\u2028\"],\"z\":1}", Encoding.UTF8.GetString(bytes.Span));
    }

    [Fact]
    public void V2_ComputesAndVerifiesLogicalIdentityFromPersistedJson()
    {
        var persisted = Encoding.UTF8.GetBytes("{\"z\":1.00,\"a\":true}");
        using var equivalent = JsonDocument.Parse("{\"a\":true,\"z\":1}");

        var hash = CanonicalJsonPayloadSerializerV2.ComputeSha256(persisted);

        Assert.Equal(CanonicalJsonPayloadSerializerV2.ComputeSha256(equivalent.RootElement), hash);
        Assert.True(CanonicalJsonPayloadSerializerV2.VerifySha256(persisted, hash));
        Assert.True(CanonicalJsonPayloadSerializerV2.VerifySha256(equivalent.RootElement, hash));
        Assert.False(CanonicalJsonPayloadSerializerV2.VerifySha256(
            Encoding.UTF8.GetBytes("{\"a\":false,\"z\":1}"),
            hash));
        Assert.Throws<ArgumentException>(() =>
            CanonicalJsonPayloadSerializerV2.VerifySha256(persisted, hash.ToUpperInvariant()));
    }

    [Fact]
    public void V2_MatchesIndependentPortableVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "penghou-canonical-json-v2.json");
        var vectors = JsonSerializer.Deserialize<CanonicalVector[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (var vector in vectors)
        {
            using var input = JsonDocument.Parse(vector.InputJson);
            var bytes = CanonicalJsonPayloadSerializerV2.Canonicalize(input.RootElement);
            Assert.Equal(vector.CanonicalJson, Encoding.UTF8.GetString(bytes.Span));
            Assert.Equal(vector.Sha256, Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
        }
    }

    private sealed record CanonicalVector(string Name, string InputJson, string CanonicalJson, string Sha256);

    [Fact]
    public void Serialize_WithSourceGeneratedMetadata_MatchesDefaultContract()
    {
        var value = new SampleValue(42);

        var v1Expected = new CanonicalJsonPayloadSerializer().Serialize(value);
        var v1Actual = new CanonicalJsonPayloadSerializer().Serialize(
            value, SampleJsonContext.Default.SampleValue);
        var v2Expected = new CanonicalJsonPayloadSerializerV2().Serialize(value);
        var v2Actual = new CanonicalJsonPayloadSerializerV2().Serialize(
            value, SampleJsonContext.Default.SampleValue);

        Assert.Equal(v1Expected.Bytes.ToArray(), v1Actual.Bytes.ToArray());
        Assert.Equal(v2Expected.Bytes.ToArray(), v2Actual.Bytes.ToArray());
        Assert.Equal("{\"value\":42}", Encoding.UTF8.GetString(v2Actual.Bytes.Span));
    }

    internal sealed record SampleValue(int Value);

    [JsonSerializable(typeof(SampleValue))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class SampleJsonContext : JsonSerializerContext;
}
