using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Penghou.Siming;

/// <summary>Serializes values using the deterministic Penghou canonical JSON v1 contract.</summary>
/// <param name="serializerOptions">Options for the initial CLR-to-JSON conversion.</param>
public sealed class CanonicalJsonPayloadSerializer(JsonSerializerOptions? serializerOptions = null) : ILedgerPayloadSerializer
{
    /// <summary>Stable serialization format identifier.</summary>
    public const string Format = "penghou-canonical-json";
    /// <summary>Canonical JSON format version.</summary>
    public const int Version = 1;
    private readonly JsonSerializerOptions options = serializerOptions is null ? new(JsonSerializerDefaults.Web) : new(serializerOptions);

    /// <inheritdoc />
    [RequiresUnreferencedCode("Serializing the payload type may require members that cannot be statically analyzed. Use the JsonTypeInfo<T> overload for trimmed or Native AOT applications.")]
    [RequiresDynamicCode("Serializing the payload type may require dynamic code generation. Use the JsonTypeInfo<T> overload for trimmed or Native AOT applications.")]
    public SerializedLedgerPayload Serialize<T>(T payload) => new(Canonicalize(JsonSerializer.SerializeToElement(payload, options)), "application/json", Format, Version);

    /// <summary>Serializes one payload with source-generated metadata without persisting it.</summary>
    public SerializedLedgerPayload Serialize<T>(T payload, JsonTypeInfo<T> jsonTypeInfo) => new(Canonicalize(JsonSerializer.SerializeToElement(payload, jsonTypeInfo)), "application/json", Format, Version);

    /// <summary>Produces deterministic UTF-8 JSON bytes from a JSON tree.</summary>
    public static ReadOnlyMemory<byte> Canonicalize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false })) Write(writer, element);
        return buffer.WrittenMemory.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number: WriteNumber(writer, element); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new JsonException($"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(decimalValue == 0
                ? "0"
                : decimalValue.ToString("G29", CultureInfo.InvariantCulture));
            return;
        }
        var value = element.GetDouble();
        if (!double.IsFinite(value)) throw new JsonException("Non-finite JSON numbers are unsupported.");
        if (value == 0)
        {
            writer.WriteRawValue("0");
            return;
        }
        var formatted = value.ToString("R", CultureInfo.InvariantCulture)
            .Replace("E+", "e", StringComparison.Ordinal)
            .Replace("E", "e", StringComparison.Ordinal);
        var exponent = formatted.IndexOf('e');
        if (exponent >= 0)
        {
            var prefix = formatted[..(exponent + 1)];
            var suffix = formatted[(exponent + 1)..];
            var negative = suffix.StartsWith("-", StringComparison.Ordinal);
            suffix = suffix.TrimStart('+', '-').TrimStart('0');
            if (suffix.Length == 0)
                suffix = "0";
            formatted = prefix + (negative ? "-" : string.Empty) + suffix;
        }
        writer.WriteRawValue(formatted);
    }
}
