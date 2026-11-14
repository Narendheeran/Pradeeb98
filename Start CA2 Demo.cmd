@echo off
setlocal
pushd "%~dp0" || exit /b 1
echo CA2 simulated dashboard: http://127.0.0.1:5188/dashboard
call dotnet run --project PW7_MudBlazor -c Release --no-launch-profile -- --urls http://127.0.0.1:5188 --Mqtt:DemoMode=true
set "ca2ExitCode=%ERRORLEVEL%"
popd
if not "%ca2ExitCode%"=="0" pause
exit /b %ca2ExitCode%
