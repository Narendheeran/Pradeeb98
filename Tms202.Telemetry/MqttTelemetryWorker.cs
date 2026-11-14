using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Tms202.Telemetry;

public class MqttTelemetryWorker(TelemetryStore store, IOptions<MqttOptions> settings, ILogger<MqttTelemetryWorker> logger) : BackgroundService
{
    private IMqttClient? _client;
    private int _demoEnabled = 1;
    private int _demoHold;

    /// <summary>Publishes a request only. A later telemetry packet must confirm PLC state.</summary>
    public async Task PublishControlAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (settings.Value.DemoMode)
        {
            if (store.Snapshot.Reading is not { Np9Pressed: false, Np9Hold: false } || Volatile.Read(ref _demoHold) == 1)
                throw new InvalidOperationException("Release NP9 and reset the inspection hold first.");
            Interlocked.Exchange(ref _demoEnabled, enabled ? 1 : 0);
            return;
        }
        store.EvaluateFreshness(DateTimeOffset.UtcNow);
        if (!store.Snapshot.CanControl)
            throw new InvalidOperationException("Control requires a broker connection and fresh, valid telemetry.");
        await PublishCommandAsync(enabled ? "1" : "0", cancellationToken);
    }

    public async Task ResetInspectionHoldAsync(CancellationToken cancellationToken = default)
    {
        if (settings.Value.DemoMode)
        {
            if (store.Snapshot.Reading is not { Np9Pressed: false, Np9Hold: true })
                throw new InvalidOperationException("Reset requires NP9 to be released and the inspection hold active.");
            Interlocked.Exchange(ref _demoEnabled, 0);
            Interlocked.Exchange(ref _demoHold, 0);
            return;
        }
        store.EvaluateFreshness(DateTimeOffset.UtcNow);
        if (!store.Snapshot.CanResetInspectionHold)
            throw new InvalidOperationException("Reset requires fresh telemetry confirming NP9 is released and the inspection hold is active.");
        await PublishCommandAsync("R", cancellationToken);
    }

    private async Task PublishCommandAsync(string payload, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
            throw new InvalidOperationException("MQTT connection unavailable.");
        var message = new MqttApplicationMessageBuilder().WithTopic(settings.Value.CommandTopic)
            .WithPayload(payload).WithRetainFlag(false)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var result = await client.PublishAsync(message, cancellationToken);
        if ((int)result.ReasonCode >= 128) throw new InvalidOperationException("Broker rejected the control request.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        options.Validate();
        if (options.DemoMode) { await RunDemoAsync(stoppingToken); return; }

        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var freshness = TrackFreshnessAsync(monitoring.Token);
        try { await RunLiveAsync(options, stoppingToken); }
        finally
        {
            await monitoring.CancelAsync();
            await freshness;
        }
    }

    private async Task TrackFreshnessAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                store.EvaluateFreshness(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RunLiveAsync(MqttOptions options, CancellationToken stoppingToken)
    {

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        _client = client;
        client.ApplicationMessageReceivedAsync += message =>
        {
            // A retained replay has no verified sample age and cannot authorize control.
            if (message.ApplicationMessage.Retain) return Task.CompletedTask;
            var payload = message.ApplicationMessage.ConvertPayloadToString();
            if (ReadingParser.TryParse(payload, out var reading, out var error)) store.UpdateReading(reading!);
            else { store.Reject(error); logger.LogWarning("Rejected MQTT payload: {Reason}", error); }
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += _ =>
        {
            if (!stoppingToken.IsCancellationRequested) store.SetConnection("Reconnecting", "Broker connection lost. Retrying automatically.");
            return Task.CompletedTask;
        };

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId($"{options.ClientIdPrefix}-{Guid.NewGuid():N}")
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(options.Username)) clientOptions.WithCredentials(options.Username, options.Password);
        if (options.UseTls) clientOptions.WithTlsOptions(tls => tls.UseTls());

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    store.SetConnection("Connecting");
                    var result = await client.ConnectAsync(clientOptions.Build(), stoppingToken);
                    if (result.ResultCode != MqttClientConnectResultCode.Success)
                        throw new InvalidOperationException($"Broker refused connection: {result.ResultCode}.");
                    var subscription = factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter.WithTopic(options.Topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build();
                    var subscribed = await client.SubscribeAsync(subscription, stoppingToken);
                    if (!subscribed.Items.Any() || subscribed.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("Broker rejected the topic subscription. Check topic permissions.");
                    store.SetConnection("Listening");
                    logger.LogInformation("Subscribed. Listening on {Topic}", options.Topic);
                    while (client.IsConnected && !stoppingToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Credentials and raw payloads are deliberately not written to logs.
                    store.SetConnection("Reconnecting", "Unable to receive MQTT data. Check broker, credentials and topic.");
                    logger.LogWarning("MQTT connection interrupted ({ErrorType}); retrying.", ex.GetType().Name);
                    await TryDisconnectAsync(client);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.ReconnectSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await TryDisconnectAsync(client);
            store.SetConnection("Stopped");
            _client = null;
        }
    }

    private static async Task TryDisconnectAsync(IMqttClient client)
    {
        if (!client.IsConnected) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token); }
        catch (Exception) { /* The connection may already have disappeared; retry/stop still continues. */ }
    }

    private async Task RunDemoAsync(CancellationToken cancellationToken)
    {
        store.SetConnection("Demo");
        var step = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var detected = step % 12 >= 6;
                var np9Pressed = step % 18 is 5 or 6;
                if (np9Pressed)
                {
                    Interlocked.Exchange(ref _demoHold, 1);
                    Interlocked.Exchange(ref _demoEnabled, 0);
                }
                var inspectionHold = Volatile.Read(ref _demoHold) == 1;
                var enabled = !inspectionHold && Volatile.Read(ref _demoEnabled) == 1;
                var payload = JsonSerializer.Serialize(new
                {
                    device_id = "CA2-DEMO", device_name = "Packaging station simulation",
                    voltage = Math.Round(5 + Math.Sin(step / 6.0) * 3.2, 2),
                    sensor_pnp = detected ? 1 : 0, led_state = detected && enabled ? 1 : 0, enable_control = enabled ? 1 : 0,
                    np9_pressed = np9Pressed,
                    np9_hold = inspectionHold,
                    temperature_c = Math.Round(settings.Value.TemperatureHighC + Math.Sin(step / 5.0) * 4.5 - 1.0, 1),
                    temperature_ok = true
                });
                if (ReadingParser.TryParse(payload, out var reading, out _)) store.UpdateReading(reading!);
                step++;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { store.SetConnection("Stopped"); }
    }
}
