using Microsoft.Extensions.Options;
using Tms202.Telemetry;

namespace PW7_MudBlazor.Services;

public sealed class MqttBackgroundService(MqttDataStore store, IOptions<MqttOptions> options, ILogger<MqttTelemetryWorker> logger)
    : MqttTelemetryWorker(store, options, logger);
