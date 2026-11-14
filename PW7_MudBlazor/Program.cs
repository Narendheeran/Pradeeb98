using System.Text;
using MudBlazor.Services;
using PW7_MudBlazor.Components;
using PW7_MudBlazor.Services;
using Tms202.Telemetry;

var builder = WebApplication.CreateBuilder(args);
var mqtt = builder.Configuration.GetSection("Mqtt");
if (!mqtt.GetValue("DemoMode", true) && mqtt.GetValue<bool>("PromptForPassword") &&
    !string.IsNullOrWhiteSpace(mqtt["Username"]) && string.IsNullOrEmpty(mqtt["Password"]))
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Start PW7 in a terminal to enter the MQTT password, or supply Mqtt__Password in the process environment.");
        return 1;
    }

    Console.Write("Beaver MQTT password (input hidden): ");
    var password = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
    if (password.Length == 0)
    {
        Console.Error.WriteLine("Enter the Beaver MQTT password to start PW7.");
        return 1;
    }
    mqtt["Password"] = password.ToString();
    password.Clear();
}
// Support local dotnet run from a Release build as well as published output.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.Configure<MqttOptions>(mqtt);
builder.Services.AddSingleton<MqttDataStore>();
builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MqttBackgroundService>());
var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
// Readings/status only; MQTT credentials are never exposed by this endpoint.
app.MapGet("/api/telemetry", (MqttDataStore store) => Results.Ok(store.Snapshot));
app.Run();
return 0;
