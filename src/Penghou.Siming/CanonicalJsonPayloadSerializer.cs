using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Penghou.Siming;

public sealed class CanonicalJsonPayloadSerializer(JsonSerializerOptions? serializerOptions = null) : ILedgerPayloadSerializer
{
    public const string Format = "penghou-canonical-json";
    public const int Version = 1;
    private readonly JsonSerializerOptions options = serializerOptions is null ? new(JsonSerializerDefaults.Web) : new(serializerOptions);

    public SerializedLedgerPayload Serialize<T>(T payload) => new(Canonicalize(JsonSerializer.SerializeToElement(payload, options)), "application/json", Format, Version);

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
        if (element.TryGetInt64(out var integer)) { writer.WriteNumberValue(integer); return; }
        if (element.TryGetDecimal(out var decimalValue)) { writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture)); return; }
        var value = element.GetDouble();
        if (!double.IsFinite(value)) throw new JsonException("Non-finite JSON numbers are unsupported.");
        writer.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e", StringComparison.Ordinal).Replace("E", "e", StringComparison.Ordinal));
    }
}
