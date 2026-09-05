using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Penghou.Siming;

/// <summary>
/// Serializes values using the non-lossy Penghou canonical JSON v2 contract.
/// </summary>
/// <remarks>
/// v2 is deliberately a new serializer type and version.  The v1 serializer
/// remains unchanged because persisted ledger rows may depend on its historical
/// number behavior.  v2 rejects duplicate object names and canonicalizes JSON
/// numbers from their source token, without converting them through
/// <see cref="double"/> or <see cref="decimal"/>.
/// </remarks>
/// <param name="serializerOptions">Options for the initial CLR-to-JSON conversion.</param>
public sealed class CanonicalJsonPayloadSerializerV2(JsonSerializerOptions? serializerOptions = null) : ILedgerPayloadSerializer
{
    /// <summary>Stable serialization format identifier.</summary>
    public const string Format = "penghou-canonical-json";

    /// <summary>Canonical JSON format version.</summary>
    public const int Version = 2;

    /// <summary>Human-readable contract name including its version.</summary>
    public const string Contract = "penghou-canonical-json-v2";

    /// <summary>Logical identity contract formed from canonical v2 bytes and SHA-256.</summary>
    public const string Sha256Contract = "penghou-canonical-json-v2-sha256";

    /// <summary>Maximum accepted JSON number token and emitted canonical number size.</summary>
    public const int MaximumNumberLength = 1_000_000;

    /// <summary>Maximum exponent digit count accepted by the bounded v2 contract.</summary>
    public const int MaximumExponentLength = 128;

    private readonly JsonSerializerOptions options = serializerOptions is null ? new(JsonSerializerDefaults.Web) : new(serializerOptions);

    /// <inheritdoc />
    public SerializedLedgerPayload Serialize<T>(T payload) =>
        new(Canonicalize(JsonSerializer.SerializeToElement(payload, options)), "application/json", Format, Version);

    /// <summary>Produces deterministic UTF-8 JSON bytes from a JSON tree.</summary>
    /// <exception cref="JsonException">The tree contains duplicate properties or an unsupported number.</exception>
    public static ReadOnlyMemory<byte> Canonicalize(JsonElement element)
    {
        ValidateTree(element);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false,
        }))
            Write(writer, element);

        return buffer.WrittenMemory.ToArray();
    }

    /// <summary>Parses persisted UTF-8 JSON and returns its canonical UTF-8 representation.</summary>
    public static ReadOnlyMemory<byte> Canonicalize(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return Canonicalize(document.RootElement);
    }

    /// <summary>Parses persisted UTF-8 JSON and returns its canonical UTF-8 representation.</summary>
    public static ReadOnlyMemory<byte> Canonicalize(ReadOnlySpan<byte> utf8Json) => Canonicalize((ReadOnlyMemory<byte>)utf8Json.ToArray());

    /// <summary>Computes SHA-256 over the canonical v2 representation of a JSON tree.</summary>
    public static string ComputeSha256(JsonElement element) =>
        Convert.ToHexString(SHA256.HashData(Canonicalize(element).Span)).ToLowerInvariant();

    /// <summary>Computes SHA-256 over the canonical v2 representation of persisted UTF-8 JSON.</summary>
    public static string ComputeSha256(ReadOnlyMemory<byte> utf8Json) =>
        Convert.ToHexString(SHA256.HashData(Canonicalize(utf8Json).Span)).ToLowerInvariant();

    /// <summary>Verifies a JSON tree against a canonical v2 SHA-256 identity.</summary>
    public static bool VerifySha256(JsonElement element, string expectedHash) =>
        VerifyHash(Canonicalize(element).Span, expectedHash);

    /// <summary>Verifies persisted UTF-8 JSON against a canonical v2 SHA-256 identity.</summary>
    public static bool VerifySha256(ReadOnlyMemory<byte> utf8Json, string expectedHash) =>
        VerifyHash(Canonicalize(utf8Json).Span, expectedHash);

    private static void ValidateTree(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException($"Duplicate JSON properties are not allowed by {Contract}.");
                    ValidateTree(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) ValidateTree(item);
                break;
            case JsonValueKind.Number:
                _ = CanonicalizeNumber(element.GetRawText());
                break;
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new JsonException($"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalizeNumber(element.GetRawText()), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (raw.Length == 0 || raw.Length > MaximumNumberLength)
            throw new JsonException($"JSON number length must be between 1 and {MaximumNumberLength} characters.");

        var cursor = 0;
        var negative = raw[cursor] == '-';
        if (raw[cursor] is '-' or '+') cursor++;
        var mantissaEnd = raw.IndexOfAny(['e', 'E'], cursor);
        if (mantissaEnd < 0) mantissaEnd = raw.Length;

        var exponent = BigInteger.Zero;
        if (mantissaEnd < raw.Length)
        {
            var exponentText = raw[(mantissaEnd + 1)..];
            if (exponentText.TrimStart('+', '-').Length > MaximumExponentLength)
                throw new JsonException($"JSON number exponent cannot exceed {MaximumExponentLength} digits.");
            if (!BigInteger.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                throw new JsonException("JSON number exponent is invalid.");
        }

        var mantissa = raw[cursor..mantissaEnd];
        var point = mantissa.IndexOf('.');
        var integerLength = point < 0 ? mantissa.Length : point;
        var digits = point < 0 ? mantissa : string.Concat(mantissa.AsSpan(0, point), mantissa.AsSpan(point + 1));
        var first = 0;
        while (first < digits.Length && digits[first] == '0') first++;
        if (first == digits.Length) return "0";

        var decimalPosition = new BigInteger(integerLength) + exponent - first;
        digits = digits[first..];
        var trailing = digits.Length;
        while (trailing > 1 && digits[trailing - 1] == '0') trailing--;
        if (trailing != digits.Length) digits = digits[..trailing];

        var scientificExponent = decimalPosition - 1;
        var result = scientificExponent >= -6 && scientificExponent < 21
            ? ToFixed(digits, decimalPosition)
            : ToScientific(digits, scientificExponent);
        if (negative) result = "-" + result;
        if (result.Length > MaximumNumberLength)
            throw new JsonException($"Canonical JSON number exceeds {MaximumNumberLength} characters.");
        return result;
    }

    private static bool VerifyHash(ReadOnlySpan<byte> canonicalBytes, string expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        if (expectedHash.Length != 64
            || expectedHash.Any(character =>
                !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException(
                "A canonical SHA-256 identity must contain 64 lowercase hexadecimal characters.",
                nameof(expectedHash));
        }

        var expected = Convert.FromHexString(expectedHash);

        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(canonicalBytes, actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string ToFixed(string digits, BigInteger decimalPosition)
    {
        var position = (int)decimalPosition;
        if (position <= 0) return "0." + new string('0', -position) + digits;
        if (position >= digits.Length) return digits + new string('0', position - digits.Length);
        return digits[..position] + "." + digits[position..];
    }

    private static string ToScientific(string digits, BigInteger exponent)
    {
        var exponentText = exponent.ToString(CultureInfo.InvariantCulture);
        var negative = exponentText.StartsWith("-", StringComparison.Ordinal);
        var magnitude = negative ? exponentText[1..] : exponentText;
        if (magnitude.Length == 1) magnitude = "0" + magnitude;
        var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        return mantissa + "E" + (negative ? "-" : "+") + magnitude;
    }
}
