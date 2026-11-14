param(
    [switch]$Live,
    [string]$BrokerHost = '127.0.0.1',
    [int]$BrokerPort = 1883,
    [string]$Topic = 'beaver-iot/mqtt@default/mqtt-device/PLC_111',
    [string]$CommandTopic = 'tms202/PLC_111/control/enable'
)
$ErrorActionPreference = 'Stop'
$tmsMode = if ($Live) { 'false' } else { 'true' }
Push-Location -LiteralPath $PSScriptRoot
try {
    Write-Host 'Dashboard: http://127.0.0.1:5188/dashboard'
    if (-not $Live) { Write-Host 'DEMONSTRATION MODE: simulated readings only.' }
    & dotnet run --project PW7_MudBlazor -c Release --no-launch-profile -- --urls http://127.0.0.1:5188 "--Mqtt:DemoMode=$tmsMode" "--Mqtt:RequireTemperature=$($Live.IsPresent.ToString().ToLowerInvariant())" "--Mqtt:Host=$BrokerHost" "--Mqtt:Port=$BrokerPort" "--Mqtt:Topic=$Topic" "--Mqtt:CommandTopic=$CommandTopic"
    if ($LASTEXITCODE -ne 0) { throw "Dashboard exited with code $LASTEXITCODE." }
}
finally { Pop-Location }
