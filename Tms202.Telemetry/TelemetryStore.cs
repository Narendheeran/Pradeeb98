using Microsoft.Extensions.Options;

namespace Tms202.Telemetry;

public sealed record TelemetryEvent(DateTimeOffset At, string Kind, bool Active, string Message, string Severity);

public sealed record TelemetrySnapshot(DeviceReading? Reading, string ConnectionStatus,
    long ReceivedCount, long InvalidCount, string? LastError, DeviceReading[] History)
{
    public TelemetryEvent[] Events { get; init; } = [];
    public TelemetryEvent[] ActiveEvents { get; init; } = [];
    public bool TemperatureHigh { get; init; }
    public double? LastGoodTemperatureC { get; init; }
    public bool IsCurrentLiveReadingFresh { get; init; }
    public bool CanControl { get; init; }
    public bool CanResetInspectionHold { get; init; }
}

/// <summary>Immutable snapshots prevent UI/background-thread races.</summary>
public class TelemetryStore(IOptions<MqttOptions>? settings = null)
{
    private readonly MqttOptions _options = settings?.Value ?? new MqttOptions();
    private readonly object _gate = new();
    private DateTimeOffset? _listeningSince;
    private DateTimeOffset? _lastLivePacket;
    private TelemetrySnapshot _snapshot = new(null, "Starting", 0, 0, null, []);
    public event Action? OnChange;
    public TelemetrySnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public void UpdateReading(DeviceReading reading)
    {
        lock (_gate)
        {
            var history = _snapshot.History.Append(reading).TakeLast(60).ToArray();
            _snapshot = _snapshot with { Reading = reading, History = history, ReceivedCount = _snapshot.ReceivedCount + 1, LastError = null };
            if (_snapshot.ConnectionStatus == "Listening") _lastLivePacket = reading.ReceivedAt;
            var now = reading.ReceivedAt;
            SetEvent("Payload", false, now, "Valid telemetry received.", "Warning");
            if (_snapshot.ConnectionStatus == "Listening")
                SetEvent("Telemetry", false, now, "Fresh PLC telemetry resumed.", "Error");

            var temperatureFault = reading.TemperatureOk == false ||
                (_options.RequireTemperature && reading.TemperatureOk != true);
            SetEvent("Temperature input", temperatureFault, now,
                temperatureFault ? "Temperature input is unavailable or has unverified quality." : "Temperature input recovered.", "Error");
            if (reading.TemperatureOk == true && reading.TemperatureC is { } temperature)
            {
                var alarm = _snapshot.TemperatureHigh
                    ? temperature >= _options.TemperatureHighC - _options.TemperatureHysteresisC
                    : temperature >= _options.TemperatureHighC;
                _snapshot = _snapshot with { TemperatureHigh = alarm, LastGoodTemperatureC = temperature };
                SetEvent("High temperature", alarm, now,
                    alarm ? $"Temperature reached the {_options.TemperatureHighC:0.##} °C threshold."
                          : $"Temperature fell below the {_options.TemperatureHighC - _options.TemperatureHysteresisC:0.##} °C clear point.", "Warning");
            }
            RefreshControl(now);
        }
        OnChange?.Invoke();
    }

    public void SetConnection(string status, string? error = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_snapshot.ConnectionStatus != status)
            {
                _listeningSince = status == "Listening" ? now : null;
                _lastLivePacket = null;
            }
            _snapshot = _snapshot with { ConnectionStatus = status, LastError = error };
            if (status == "Listening")
                SetEvent("Broker", false, now, "MQTT subscription restored.", "Error");
            else if (status == "Reconnecting")
                SetEvent("Broker", true, now, "MQTT connection unavailable. Reconnecting.", "Error");
            RefreshControl(now);
        }
        OnChange?.Invoke();
    }

    public void Reject(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { InvalidCount = _snapshot.InvalidCount + 1, LastError = error };
            var now = DateTimeOffset.UtcNow;
            SetEvent("Payload", true, now, $"Rejected telemetry. {error}", "Warning");
            RefreshControl(now);
        }
        OnChange?.Invoke();
    }

    public void EvaluateFreshness(DateTimeOffset now)
    {
        TelemetrySnapshot previous;
        lock (_gate)
        {
            previous = _snapshot;
            if ((_lastLivePacket ?? _listeningSince) is { } since && now - since >= TimeSpan.FromSeconds(_options.StaleAfterSeconds))
                SetEvent("Telemetry", true, now, $"No fresh PLC reading for {_options.StaleAfterSeconds} seconds.", "Error");
            RefreshControl(now);
        }
        if (!ReferenceEquals(previous, Snapshot)) OnChange?.Invoke();
    }

    private void RefreshControl(DateTimeOffset now)
    {
        var fresh = _snapshot.ConnectionStatus == "Listening" && _lastLivePacket is { } last &&
            now - last < TimeSpan.FromSeconds(_options.StaleAfterSeconds);
        var accepted = fresh && !_snapshot.ActiveEvents.Any(entry => entry.Kind == "Payload");
        var resetAvailable = accepted && _snapshot.Reading is { Np9Pressed: false, Np9Hold: true };
        var controlAvailable = accepted && _snapshot.Reading is { Np9Pressed: not true, Np9Hold: not true } &&
            !_snapshot.ActiveEvents.Any(entry => entry.Kind == "Temperature input");
        if (fresh != _snapshot.IsCurrentLiveReadingFresh || controlAvailable != _snapshot.CanControl ||
            resetAvailable != _snapshot.CanResetInspectionHold)
        {
            _snapshot = _snapshot with
            {
                IsCurrentLiveReadingFresh = fresh,
                CanControl = controlAvailable,
                CanResetInspectionHold = resetAvailable
            };
        }
    }

    private void SetEvent(string kind, bool active, DateTimeOffset at, string message, string severity)
    {
        var activeEvents = _snapshot.ActiveEvents;
        if (activeEvents.Any(entry => entry.Kind == kind) == active) return;
        var entry = new TelemetryEvent(at, kind, active, message, severity);
        _snapshot = _snapshot with
        {
            Events = _snapshot.Events.Prepend(entry).Take(50).ToArray(),
            ActiveEvents = active ? activeEvents.Prepend(entry).ToArray() : activeEvents.Where(item => item.Kind != kind).ToArray()
        };
    }
}
