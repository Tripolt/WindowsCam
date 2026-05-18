# windowsCam

windowsCam turns an iPhone into a high-quality cabled camera source for OBS on Windows.

## What is implemented

- iPhone app:
  - rear camera capture with AVFoundation
  - continuous autofocus, auto exposure, and auto white balance
  - 4K30 capture when supported, falling back to 1080p/high presets
  - hardware H.264 encoding with VideoToolbox
  - TCP stream on port `48650`
  - length-prefixed protocol with a JSON hello packet followed by H.264 Annex B frames

- Windows receiver:
  - WinForms app targeting `.NET 8`
  - launches `iproxy.exe` to bridge USB to the iPhone app
  - launches `ffmpeg.exe` to publish the H.264 stream to OBS as MPEG-TS over UDP
  - OBS input URL: `udp://127.0.0.1:48651`
  - includes an Inno Setup installer packaging path

## iPhone setup

1. Open `windowsCam/windowsCam.xcodeproj` in Xcode.
2. Select your personal development team.
3. Connect your iPhone by cable and trust the computer.
4. Build and run the `windowsCam` scheme on the iPhone.
5. Keep the app open and press Start if it is not already running.

## Windows setup for users

1. Run the `WindowsCamReceiverSetup-*.exe` installer.
2. Install OBS Studio if needed.
3. Install or place these tools either in `PATH` or in the installed app's `Tools` folder:
   - `iproxy.exe`
   - `idevice_id.exe` and `idevicename.exe` from libimobiledevice, optional but useful for device detection
   - `ffmpeg.exe`
4. Run WindowsCamReceiver and click Start.
5. In OBS, add a Media Source and set the input to:

```text
udp://127.0.0.1:48651
```

Then start OBS Virtual Camera if you want other Windows apps to see it.

For lowest latency in OBS, disable `Local File`, set `Input Format` to `mpegts` if available, and set `Network Buffering` / `Netzwerkpufferung` to `0 MB`. The receiver is designed to drop stale frames instead of accumulating seconds of delay.

## Building the Windows installer

Run this on Windows:

```powershell
.\packaging\Build-Installer.ps1
```

Requirements:

- .NET 8 SDK
- Inno Setup 6

The installer output is written to `packaging\dist\installer`.

The app is published self-contained, so the installed receiver does not require a separate .NET runtime. To bundle helper tools into the installer, place `ffmpeg.exe`, `iproxy.exe`, and optional libimobiledevice helper executables in `WindowsCamReceiver\Tools` before packaging, or run:

```powershell
.\packaging\Build-Installer.ps1 -IncludeLocalTools
```

For a portable build without an installer:

```powershell
.\packaging\Build-Installer.ps1 -SkipInstaller
```

## Protocol

Every packet starts with a 4-byte unsigned big-endian payload length.

The first packet is JSON:

```json
{
  "version": 1,
  "codec": "h264-annexb",
  "width": 3840,
  "height": 2160,
  "fps": 30,
  "orientation": "landscapeRight",
  "framing": "uint32be-length-prefixed"
}
```

All following packets contain H.264 Annex B access units. Keyframes include SPS/PPS parameter sets.
