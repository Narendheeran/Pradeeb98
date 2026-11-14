using System.Globalization;
using System.Text.Json;

namespace Tms202.Telemetry;

/// <summary>Parses the PW5 PLC payload. Optional control fields support the extended PLC project.</summary>
public static class ReadingParser
{
    public static bool TryParse(string payload, out DeviceReading? reading, out string error)
    {
        reading = null;
        error = "";
        if (payload.Length > 32768) { error = "Payload exceeds 32 KB."; return false; }
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object.");
            if (!root.TryGetProperty("device_id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString())) throw new FormatException("device_id must be a non-empty string.");
            var voltageOk = OptionalBool(root, "voltage_ok");
            double? voltage = ReadVoltage(root, voltageOk);
            if (!root.TryGetProperty("sensor_pnp", out var sensor) || !TryBool(sensor, out var detected))
                throw new FormatException("sensor_pnp must be 0, 1, false, or true.");
            var name = root.TryGetProperty("device_name", out var deviceName) && deviceName.ValueKind == JsonValueKind.String
                ? deviceName.GetString() ?? "WAGO CC100" : "WAGO CC100";
            double? temperature = null;
            if (root.TryGetProperty("temperature_c", out var sample) && sample.ValueKind != JsonValueKind.Null)
            {
                if (!TryDouble(sample, out var degrees) || !double.IsFinite(degrees))
                    throw new FormatException("temperature_c must be a finite number or null.");
                temperature = degrees;
            }
            var temperatureOk = OptionalBool(root, "temperature_ok");
            if (temperatureOk == true && temperature is null)
                throw new FormatException("temperature_ok requires a numeric temperature_c when true.");
            reading = new DeviceReading(id.GetString()!, name, voltage, detected,
                OptionalBool(root, "led_state"), OptionalBool(root, "enable_control"), DateTimeOffset.UtcNow)
            {
                TemperatureC = temperature,
                TemperatureOk = temperatureOk,
                Np9Pressed = OptionalBool(root, "np9_pressed"),
                Np9Hold = OptionalBool(root, "np9_hold"),
                VoltageOk = voltageOk,
                PublishSequence = root.TryGetProperty("seq", out var sequence) && sequence.ValueKind == JsonValueKind.Number &&
                    sequence.TryGetUInt32(out var parsedSequence) ? parsedSequence : null
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { error = ex is JsonException ? "Malformed JSON." : ex.Message; return false; }
    }

    private static double? ReadVoltage(JsonElement root, bool? voltageOk)
    {
        if (root.TryGetProperty("voltage", out var voltage) && TryDouble(voltage, out var value) && double.IsFinite(value))
            return value;
        if (voltageOk == false && voltage.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        throw new FormatException("voltage must be a finite number unless voltage_ok is false and voltage is missing or null.");
    }

    private static bool TryDouble(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out result)
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool? OptionalBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;
        if (TryBool(value, out var result)) return result;
        throw new FormatException($"{property} must be 0, 1, false, or true.");
    }

    private static bool TryBool(JsonElement value, out bool result)
    {
        result = false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) { result = value.GetBoolean(); return true; }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is 0 or 1)
        { result = number == 1; return true; }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text is "0" or "1") { result = text == "1"; return true; }
            return bool.TryParse(text, out result);
        }
        return false;
    }
}
