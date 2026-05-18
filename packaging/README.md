# Windows Installer Packaging

Run these commands on Windows from the repository root.

## Build a single installer

```powershell
.\packaging\Build-Installer.ps1
```

The output installer is written to:

```text
packaging\dist\installer
```

## Requirements

- .NET 8 SDK
- Inno Setup 6, unless using `-SkipInstaller`

The published app is self-contained, so people who install the receiver do not need to install the .NET runtime separately.

## Bundling helper tools

The receiver needs `ffmpeg.exe` and `iproxy.exe` at runtime. Optional device-name helpers are `idevice_id.exe` and `idevicename.exe`.

To bundle tools into the installer, place them in:

```text
WindowsCamReceiver\Tools
```

Then run:

```powershell
.\packaging\Build-Installer.ps1
```

If the tools are already on your packaging machine's `PATH`, you can copy them into the installer automatically:

```powershell
.\packaging\Build-Installer.ps1 -IncludeLocalTools
```

## Publish without Inno Setup

```powershell
.\packaging\Build-Installer.ps1 -SkipInstaller
```

This creates a portable published app under:

```text
packaging\dist\publish\win-x64
```
