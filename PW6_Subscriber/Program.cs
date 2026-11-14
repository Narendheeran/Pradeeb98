using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tms202.Telemetry;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddHostedService<MqttTelemetryWorker>();
using var host = builder.Build();
var store = host.Services.GetRequiredService<TelemetryStore>();
long lastCount = 0;
string previousStatus = "";
store.OnChange += () =>
{
    var state = store.Snapshot;
    if (state.ConnectionStatus != previousStatus)
    {
        previousStatus = state.ConnectionStatus;
        Console.WriteLine($"Status: {state.ConnectionStatus}");
        if (state.ConnectionStatus == "Demo") Console.WriteLine("DEMONSTRATION MODE — simulated values, not a physical PLC connection.");
        if (state.ConnectionStatus == "Listening") Console.WriteLine("Subscribed. Listening...");
    }
    if (state.ReceivedCount != lastCount && state.Reading is { } reading)
    {
        lastCount = state.ReceivedCount;
        var voltage = reading.DisplayVoltage is { } value ? $"{value:0.00} V" : "unavailable";
        Console.WriteLine($"{reading.ReceivedAt:O} | device={reading.DeviceId} | voltage={voltage} | sensor_pnp={(reading.SensorPnp ? 1 : 0)} | np9_pressed={reading.Np9Pressed} | led_state={reading.LedState} | enable_control={reading.EnableControl} | temperature_c={reading.TemperatureC:0.0} | temperature_ok={reading.TemperatureOk}");
    }
};
Console.WriteLine("PW6 MQTT subscriber. Press Ctrl+C to stop.");
await host.RunAsync();
