using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Penghou.Siming.Tests;

public sealed class CanonicalJsonPayloadSerializerTests
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

    private sealed record CanonicalVector(string Name, string InputJson, string CanonicalJson, string Sha256);
}
