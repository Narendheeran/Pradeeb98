namespace Tms202.Telemetry;

public sealed record DeviceReading(
    string DeviceId, string DeviceName, double? Voltage, bool SensorPnp,
    bool? LedState, bool? EnableControl, DateTimeOffset ReceivedAt)
{
    public double? TemperatureC { get; init; }
    public bool? TemperatureOk { get; init; }
    public bool? Np9Pressed { get; init; }
    public bool? Np9Hold { get; init; }
    public bool? VoltageOk { get; init; }
    public uint? PublishSequence { get; init; }

    public double? DisplayVoltage => VoltageOk == false ? null : Voltage;
}
