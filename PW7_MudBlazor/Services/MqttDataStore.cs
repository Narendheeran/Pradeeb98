using Microsoft.Extensions.Options;
using Tms202.Telemetry;

namespace PW7_MudBlazor.Services;

public sealed class MqttDataStore(IOptions<MqttOptions> settings) : TelemetryStore(settings);
