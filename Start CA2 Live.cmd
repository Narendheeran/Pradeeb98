@echo off
setlocal
pushd "%~dp0" || exit /b 1
echo CA2 live dashboard: http://127.0.0.1:5189/dashboard
call dotnet run --project PW7_MudBlazor -c Release --no-launch-profile -- --urls http://127.0.0.1:5189 --Mqtt:DemoMode=false --Mqtt:RequireTemperature=true --Mqtt:Host=192.168.101.59 --Mqtt:Port=1883 --Mqtt:Topic=beaver-iot/mqtt@default/mqtt-device/PLC_111 --Mqtt:CommandTopic=tms202/PLC_111/control/enable --Mqtt:Username=mqtt@default --Mqtt:PromptForPassword=true
set "pw7ExitCode=%ERRORLEVEL%"
popd
if not "%pw7ExitCode%"=="0" pause
exit /b %pw7ExitCode%
