# CA2 software verification

On 14 November 2026 the four-project solution built in Release mode with .NET SDK 10.0.401. The build reported zero warnings and zero errors. All 31 automated assertions passed.

| Check | Result |
| --- | --- |
| Telemetry | Existing CA1 messages remain readable. Valid CA2 samples carry temperature and a quality flag. Missing or bad quality disables live control when CA2 temperature is required. |
| Threshold | A valid 40 °C sample started the alert. It held at 39 °C and cleared at 38.9 °C. A bad probe sample preserved the last valid temperature and the active warning. |
| Faults | Local checks observed stale telemetry while the broker stayed connected, rejection of malformed JSON and reconnect with recovery events. |
| Commands | The local broker received non-retained, QoS 1 ASCII `0` and `1` on the command topic. |
| Dashboard | The demo page, stylesheet and telemetry API returned HTTP 200. The simulated API reported the high-temperature state at the default threshold. |

The tests used generated inputs and an MQTT broker on the PC. Live CA2 checks for probe accuracy, hardware output response, broker security and the physical fault procedure are pending in `../CA2_Lab_Validation.md`.

To repeat the software checks from this folder:

```powershell
dotnet restore .\TMS202.sln --locked-mode
dotnet build .\TMS202.sln -c Release --no-restore
dotnet run --project .\Tms202.Tests -c Release --no-build
```
