namespace Tms202.Telemetry;

public sealed class MqttOptions
{
    public bool DemoMode { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public string Topic { get; set; } = "beaver-iot/mqtt@default/mqtt-device/Dev001";
    public string ClientIdPrefix { get; set; } = "tms202";
    public string CommandTopic { get; set; } = "tms202/Dev001/control/enable";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public int ReconnectSeconds { get; set; } = 3;
    public int StaleAfterSeconds { get; set; } = 15;
    public bool RequireTemperature { get; set; }
    public double TemperatureHighC { get; set; } = 40;
    public double TemperatureHysteresisC { get; set; } = 1;
    public double VoltageMaximum { get; set; } = 10;
    public string WebVisuUrl { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Mqtt:Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Mqtt:Port must be 1–65535.");
        if (string.IsNullOrWhiteSpace(Topic)) throw new ArgumentException("Mqtt:Topic is required.");
        if (string.IsNullOrWhiteSpace(CommandTopic) || CommandTopic.IndexOfAny(['#', '+']) >= 0)
            throw new ArgumentException("Mqtt:CommandTopic must be a concrete publish topic.");
        if (ReconnectSeconds < 1 || StaleAfterSeconds < 1) throw new ArgumentException("MQTT time intervals must be positive.");
        if (!double.IsFinite(VoltageMaximum) || VoltageMaximum <= 0) throw new ArgumentException("Mqtt:VoltageMaximum must be positive.");
        if (!double.IsFinite(TemperatureHighC) || !double.IsFinite(TemperatureHysteresisC) ||
            TemperatureHysteresisC <= 0 || !double.IsFinite(TemperatureHighC - TemperatureHysteresisC))
            throw new ArgumentException("Temperature threshold must be finite and hysteresis must be finite and positive.");
    }
}
