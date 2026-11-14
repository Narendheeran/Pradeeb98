using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using MQTTnet.Protocol;
using Tms202.Telemetry;

var passed = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    passed++;
    Console.WriteLine("PASS: " + message);
}
Assert(ReadingParser.TryParse("""{"device_id":"Dev001","device_name":"Tutor","voltage":4.75,"sensor_pnp":1,"led_state":0,"enable_control":false}""", out var reading, out _) &&
    reading is { DeviceId: "Dev001", Voltage: 4.75, SensorPnp: true, LedState: false, EnableControl: false }, "PLC numeric payload and optional control values");
Assert(ReadingParser.TryParse("""{"device_id":"Dev001","voltage":"2.25","sensor_pnp":"false"}""", out reading, out _) &&
    reading is { Voltage: 2.25, DisplayVoltage: 2.25, SensorPnp: false, LedState: null, Np9Pressed: null, Np9Hold: null, VoltageOk: null },
    "Legacy packet displays voltage and leaves optional NP9 and quality unreported");
Assert(ReadingParser.TryParse("""{"device_id":"PLC_001","voltage":0,"voltage_ok":1,"sensor_pnp":0,"np9_pressed":1}""", out reading, out _) &&
    reading is { DisplayVoltage: 0, VoltageOk: true, SensorPnp: false, Np9Pressed: true },
    "NP9 is independent of proximity and a valid zero remains a reading");
Assert(ReadingParser.TryParse("""{"device_id":"PLC_001","voltage":8,"voltage_ok":false,"sensor_pnp":1,"np9_pressed":0}""", out reading, out _) &&
    reading is { Voltage: 8, DisplayVoltage: null, VoltageOk: false, SensorPnp: true, Np9Pressed: false },
    "Explicit voltage fault hides the numeric sample while preserving the other readings");
foreach (var unavailable in new[] {
    """{"device_id":"PLC_001","voltage":null,"voltage_ok":0,"sensor_pnp":1}""",
    """{"device_id":"PLC_001","voltage_ok":0,"sensor_pnp":1}""" })
    Assert(ReadingParser.TryParse(unavailable, out reading, out _) && reading is { Voltage: null, DisplayVoltage: null, VoltageOk: false },
        "Missing or null voltage is accepted when the PLC reports invalid quality");
foreach (var invalid in new[] {
    """{"device_id":"Dev001","voltage":5,"sensor_pnp":2}""",
    """{"device_id":"","voltage":5,"sensor_pnp":1}""",
    """{"device_id":"Dev001","voltage":"NaN","sensor_pnp":1}""",
    """{"device_id":"Dev001","sensor_pnp":1}""",
    """{"device_id":"Dev001","voltage":null,"voltage_ok":1,"sensor_pnp":1}""",
    """{"device_id":"Dev001","voltage":5,"sensor_pnp":0,"np9_pressed":2}""",
    """{"device_id":"Dev001","voltage":5,"sensor_pnp":1,"led_state":"maybe"}""", "[1,2]", "not json", new string('x', 32769) })
    Assert(!ReadingParser.TryParse(invalid, out _, out _), "Reject invalid payload " + invalid[..Math.Min(65, invalid.Length)]);
var store = new TelemetryStore();
for (var n = 0; n < 70; n++) store.UpdateReading(new("Dev001", "Test", n, false, null, null, DateTimeOffset.UtcNow));
Assert(store.Snapshot.History.Length == 60 && store.Snapshot.History[0].Voltage == 10, "History retains latest 60 readings");
store.Reject("Bad packet");
Assert(store.Snapshot.Reading?.Voltage == 69 && store.Snapshot.InvalidCount == 1, "Malformed packet preserves latest valid reading");

Assert(ReadingParser.TryParse("""{"device_id":"PLC_001","voltage":0,"sensor_pnp":0,"temperature_c":40,"temperature_ok":1}""",
    out var temperatureReading, out _) && temperatureReading is { TemperatureC: 40, TemperatureOk: true }, "CA2 temperature and quality parse");
Assert(!ReadingParser.TryParse("""{"device_id":"PLC_001","voltage":0,"sensor_pnp":0,"temperature_c":null,"temperature_ok":true}""",
    out _, out _), "Reported good quality requires a measured temperature");
var conditionStore = new TelemetryStore(Options.Create(new MqttOptions { RequireTemperature = true, StaleAfterSeconds = 15 }));
conditionStore.SetConnection("Listening");
Assert(!conditionStore.Snapshot.CanControl, "New MQTT session requires fresh telemetry before live control");
var time = DateTimeOffset.UtcNow;
conditionStore.UpdateReading(new("PLC_001", "Test", 0, false, false, true, time));
Assert(!conditionStore.Snapshot.CanControl && conditionStore.Snapshot.ActiveEvents.Any(entry => entry.Kind == "Temperature input"),
    "CA1 payload remains readable and reports missing temperature in CA2 mode");
conditionStore.UpdateReading(temperatureReading! with { ReceivedAt = time });
Assert(conditionStore.Snapshot is { TemperatureHigh: true, CanControl: true, LastGoodTemperatureC: 40 }, "High alert triggers at 40 and a valid reading enables control");
conditionStore.UpdateReading(temperatureReading! with { ReceivedAt = time, TemperatureC = 39 });
Assert(conditionStore.Snapshot.TemperatureHigh, "High alert persists at the 39 degree clear boundary");
conditionStore.UpdateReading(temperatureReading! with { ReceivedAt = time, TemperatureC = 35, TemperatureOk = false });
Assert(conditionStore.Snapshot is { TemperatureHigh: true, CanControl: false, LastGoodTemperatureC: 39 }, "Probe fault keeps the last good value and high state and disables live control");
conditionStore.UpdateReading(temperatureReading! with { ReceivedAt = time, TemperatureC = 38.9 });
Assert(conditionStore.Snapshot is { TemperatureHigh: false, CanControl: true } &&
    conditionStore.Snapshot.Events.Any(entry => entry is { Kind: "High temperature", Active: false }), "Fresh valid sample below 39 clears high alert and logs recovery");
conditionStore.EvaluateFreshness(time.AddSeconds(15));
conditionStore.EvaluateFreshness(time.AddSeconds(16));
Assert(!conditionStore.Snapshot.CanControl && conditionStore.Snapshot.ActiveEvents.Any(entry => entry.Kind == "Telemetry") &&
    conditionStore.Snapshot.Events.Count(entry => entry is { Kind: "Telemetry", Active: true }) == 1, "Stale telemetry blocks controls and logs a single event");
conditionStore.UpdateReading(temperatureReading! with { ReceivedAt = time.AddSeconds(17), TemperatureC = 38 });
Assert(conditionStore.Snapshot.CanControl && conditionStore.Snapshot.Events.Any(entry => entry is { Kind: "Telemetry", Active: false }),
    "Fresh telemetry clears the stale warning and records recovery");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var brokerOptions = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).Build();
var serverFactory = new MqttServerFactory();
using var broker = serverFactory.CreateMqttServer(brokerOptions);
await broker.StartAsync();
var localOptions = Options.Create(new MqttOptions { DemoMode = false, Port = port, ReconnectSeconds = 1, StaleAfterSeconds = 3, RequireTemperature = true });
var liveStore = new TelemetryStore(localOptions);
using var worker = new MqttTelemetryWorker(liveStore, localOptions, NullLogger<MqttTelemetryWorker>.Instance);
await worker.StartAsync(CancellationToken.None);
await WaitUntil(() => liveStore.Snapshot.ConnectionStatus == "Listening");
using var publisher = new MqttClientFactory().CreateMqttClient();
var publisherOptions = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build();
await publisher.ConnectAsync(publisherOptions);
var topic = new MqttOptions().Topic;
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("""{"device_id":"Dev001","voltage":6.5,"sensor_pnp":1,"led_state":1,"enable_control":1,"temperature_c":22,"temperature_ok":1}""").Build());
await WaitUntil(() => liveStore.Snapshot.Reading?.Voltage == 6.5);
Assert(liveStore.Snapshot.Reading is { Voltage: 6.5, SensorPnp: true, LedState: true }, "Real local MQTT packet reaches shared data store");
string? commandPayload = null;
bool? commandRetained = null;
MqttQualityOfServiceLevel? commandQos = null;
publisher.ApplicationMessageReceivedAsync += message =>
{
    commandPayload = message.ApplicationMessage.ConvertPayloadToString();
    commandRetained = message.ApplicationMessage.Retain;
    commandQos = message.ApplicationMessage.QualityOfServiceLevel;
    return Task.CompletedTask;
};
await publisher.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic(new MqttOptions().CommandTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build());
await worker.PublishControlAsync(false);
await WaitUntil(() => commandPayload == "0");
Assert(commandPayload == "0" && commandRetained == false, "Disable command is exact ASCII 0 on command topic, non-retained");
Assert(commandQos == MqttQualityOfServiceLevel.AtLeastOnce, "Control command arrives with QoS 1");
commandPayload = null;
await worker.PublishControlAsync(true);
await WaitUntil(() => commandPayload == "1");
Assert(commandPayload == "1", "Enable command is exact ASCII 1 on command topic");
commandPayload = null;
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("""{"device_id":"Dev001","voltage":6.5,"sensor_pnp":1,"np9_pressed":1,"np9_hold":1,"enable_control":0,"temperature_c":22,"temperature_ok":1}""").Build());
await WaitUntil(() => liveStore.Snapshot.Reading?.Np9Pressed == true);
Assert(liveStore.Snapshot is { CanControl: false, CanResetInspectionHold: false, Reading: { SensorPnp: true, Np9Hold: true } },
    "Pressed NP9 blocks indicator control and hold reset while preserving proximity detection");
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("""{"device_id":"Dev001","voltage":6.5,"sensor_pnp":1,"np9_pressed":0,"np9_hold":1,"enable_control":0,"temperature_ok":0}""").Build());
await WaitUntil(() => liveStore.Snapshot.CanResetInspectionHold);
Assert(!liveStore.Snapshot.CanControl && liveStore.Snapshot.ActiveEvents.Any(item => item.Kind == "Temperature input"),
    "Released NP9 allows hold reset while an invalid probe still blocks indicator commands");
await worker.ResetInspectionHoldAsync();
await WaitUntil(() => commandPayload == "R");
Assert(commandPayload == "R" && commandRetained == false && commandQos == MqttQualityOfServiceLevel.AtLeastOnce,
    "Hold reset publishes exact ASCII R with QoS 1 and retain disabled");
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("""{"device_id":"Dev001","voltage":6.5,"sensor_pnp":1,"np9_pressed":0,"np9_hold":0,"enable_control":0,"temperature_ok":0}""").Build());
await WaitUntil(() => liveStore.Snapshot.Reading?.Np9Hold == false);
Assert(liveStore.Snapshot is { CanResetInspectionHold: false, CanControl: false, Reading.EnableControl: false },
    "A clear hold leaves the indicator disabled and the temperature interlock in force");
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("{bad}").Build());
await WaitUntil(() => liveStore.Snapshot.InvalidCount == 1);
Assert(liveStore.Snapshot.Reading?.Voltage == 6.5, "Malformed MQTT packet does not crash receiver or erase reading");
await WaitUntil(() => liveStore.Snapshot.ActiveEvents.Any(entry => entry.Kind == "Telemetry"));
Assert(!liveStore.Snapshot.CanControl && liveStore.Snapshot.ConnectionStatus == "Listening", "MQTT pause raises stale alert while broker remains connected");
await broker.StopAsync();
await WaitUntil(() => liveStore.Snapshot.ConnectionStatus != "Listening");
await broker.StartAsync();
await WaitUntil(() => liveStore.Snapshot.ConnectionStatus == "Listening");
await publisher.ConnectAsync(publisherOptions);
await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("""{"device_id":"Dev001","voltage":1.25,"sensor_pnp":0,"led_state":0,"enable_control":0,"temperature_c":22,"temperature_ok":1}""").Build());
await WaitUntil(() => liveStore.Snapshot.Reading?.Voltage == 1.25);
Assert(liveStore.Snapshot.Reading is { SensorPnp: false, LedState: false, EnableControl: false }, "Reconnect resubscribes and receives changed sensor/control states");
Assert(liveStore.Snapshot.CanControl && liveStore.Snapshot.ActiveEvents.Length == 0 &&
    liveStore.Snapshot.Events.Any(entry => entry is { Kind: "Broker", Active: false }), "Broker and data recovery clear active faults");
using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await worker.StopAsync(stopTimeout.Token);
Assert(liveStore.Snapshot.ConnectionStatus == "Stopped", "Graceful receiver shutdown");
await broker.StopAsync();
Console.WriteLine($"All {passed} assertions passed. Local broker test; no Beaver IoT or physical PLC used.");

static async Task WaitUntil(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (!predicate()) await Task.Delay(50, timeout.Token);
}
