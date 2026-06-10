# Building ThinkBook Fan Control

## Requirements

- Windows x64
- .NET 9 SDK
- Network access for first NuGet restore

## Build and Publish

Run from the repository root:

```powershell
.\scripts\build_csharp.ps1 -Configuration Release -Publish
```

Outputs:

- `dist\ThinkBookFanControl-win-x64`
  Self-contained build. Use this when the target computer may not have .NET installed.

- `dist\ThinkBookFanControl-win-x64-net9-runtime`
  Smaller build. Use this when the target computer already has .NET 9 Desktop Runtime.

## Dependency Note

The repository intentionally includes:

```text
csharp\ThinkBookFanControl\lib\LibreHardwareMonitorLib.dll
```

The app currently uses this newer local LibreHardwareMonitor DLL while the
project file explicitly packages its runtime dependencies through NuGet.
