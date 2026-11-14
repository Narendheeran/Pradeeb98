param(
    [switch]$Live,
    [string]$BrokerHost = '127.0.0.1',
    [int]$BrokerPort = 1883,
    [string]$Topic = 'beaver-iot/mqtt@default/mqtt-device/PLC_111'
)
$ErrorActionPreference = 'Stop'
$tmsMode = if ($Live) { 'false' } else { 'true' }
Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet run --project PW6_Subscriber -c Release -- "--Mqtt:DemoMode=$tmsMode" "--Mqtt:Host=$BrokerHost" "--Mqtt:Port=$BrokerPort" "--Mqtt:Topic=$Topic"
    if ($LASTEXITCODE -ne 0) { throw "Console subscriber exited with code $LASTEXITCODE." }
}
finally { Pop-Location }
