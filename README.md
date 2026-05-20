# windowsCam

windowsCam turns an iPhone into a cabled Windows 11 virtual camera named `WindowsCam`.

The camera is intended to show up in Teams, Zoom, browsers, and OBS as a normal camera source. In OBS, add it with **Video Capture Device**, not Media Source.

## What is implemented

- iPhone app:
  - rear camera capture with AVFoundation
  - continuous autofocus, auto exposure, and auto white balance
  - 1080p30 default, with 720p30 and 4K30 available
  - hardware H.264 encoding with VideoToolbox
  - TCP stream on port `48650`
  - length-prefixed protocol with a JSON hello packet followed by H.264 Annex B frames

- Windows receiver:
  - WinForms app targeting `.NET 8`
  - launches `iproxy.exe` to bridge USB to the iPhone app
  - launches `ffmpeg.exe` only as a local H.264 decoder
  - publishes latest decoded NV12 frames to `%ProgramData%\WindowsCam\latest-frame.mmf`
  - drops stale encoded frames to keep latency live
  - includes a native Windows 11 `MFCreateVirtualCamera` registration tool project

- Native virtual camera boundary:
  - camera name: `WindowsCam`
  - source CLSID: `{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}`
  - intended formats: `3840x2160 30fps`, `1920x1080 30fps`, `1280x720 30fps`, NV12
  - broker contract documented in `WindowsCam.VirtualCamera.Source`

## Current native-camera status

The receiver-side frame broker and Windows 11 virtual camera registration tool are in place. The remaining native work is the Media Foundation Custom Media Source COM DLL that implements the CLSID above and reads `%ProgramData%\WindowsCam\latest-frame.mmf`.

Windows apps will not receive video until that COM DLL is implemented, registered, and bundled with the installer.

## iPhone setup

1. Open `windowsCam/windowsCam.xcodeproj` in Xcode.
2. Select your personal development team.
3. Connect your iPhone by cable and trust the computer.
4. Build and run the `windowsCam` scheme on the iPhone.
5. Keep the app open and press Start if it is not already running.

## Windows setup for users

1. Run the WindowsCam installer as administrator.
2. Connect the iPhone by cable and trust the computer.
3. Open the iPhone app and keep it running.
4. Run WindowsCam and click Start.
5. Select `WindowsCam` in Teams, Zoom, your browser, or OBS.

For OBS, add a **Video Capture Device** and choose `WindowsCam`. Use 4K in OBS by selecting the 3840x2160 camera format when available.

## Helper tools

The receiver needs:

- `iproxy.exe`
- `ffmpeg.exe`
- optional: `idevice_id.exe` and `idevicename.exe`

Place them in the installed app's `Tools` folder or add them to `PATH`.

## Building the Windows installer

Run this on Windows:

```powershell
.\packaging\Build-Installer.ps1
```

Requirements:

- .NET 8 SDK
- Visual Studio 2022 C++ build tools
- Windows SDK with `mfvirtualcamera.h`
- Inno Setup 6

The installer output is written to `packaging\dist\installer`.

## Protocol

Every packet starts with a 4-byte unsigned big-endian payload length.

The first packet is JSON:

```json
{
  "version": 1,
  "codec": "h264-annexb",
  "width": 1920,
  "height": 1080,
  "fps": 30,
  "orientation": "landscapeRight",
  "framing": "uint32be-length-prefixed"
}
```

All following packets contain H.264 Annex B access units. Keyframes include SPS/PPS parameter sets.
