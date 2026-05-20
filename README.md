# windowsCam

windowsCam turns an iPhone into a cabled Windows 11 virtual camera named `WindowsCam`.

The camera is intended to show up in Teams, Zoom, browsers, and OBS as a normal camera source. In OBS, add it with **Video Capture Device**, not Media Source.

## What is implemented

- iPhone app:
  - rear camera capture with AVFoundation
  - continuous autofocus, auto exposure, and auto white balance
  - raw NV12 4K40 target mode, plus supported 4K30, 1080p, and 720p modes exposed by the iPhone camera
  - TCP stream on port `48650`
  - length-prefixed protocol with a JSON hello packet followed by raw-v2 NV12 frame packets

- Windows receiver:
  - WinForms app targeting `.NET 8`
  - launches `iproxy.exe` to bridge USB to the iPhone app
  - auto-starts and reconnects the raw link
  - publishes latest raw NV12 frames to `%ProgramData%\WindowsCam\latest-frame.mmf`
  - reports measured MB/s, fps, malformed frames, and USB throughput warnings
  - includes a native Windows 11 `MFCreateVirtualCamera` registration tool project

- Native virtual camera boundary:
  - camera name: `WindowsCam`
  - source CLSID: `{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}`
  - intended formats: `3840x2160 40/30fps`, `1920x1080 60/40/30fps`, `1280x720 60/40/30fps`, NV12
  - broker contract documented in `WindowsCam.VirtualCamera.Source`

## iPhone setup

1. Open `windowsCam/windowsCam.xcodeproj` in Xcode.
2. Select your personal development team.
3. Connect your iPhone by cable and trust the computer.
4. Build and run the `windowsCam` scheme on the iPhone.
5. Keep the app open; the camera starts automatically.

## Windows setup for users

1. Run the WindowsCam installer as administrator.
2. Connect the iPhone by cable and trust the computer.
3. Open the iPhone app and keep it running.
4. Run WindowsCam; the receiver starts and reconnects automatically.
5. Select `WindowsCam` in Teams, Zoom, your browser, or OBS.

For OBS, add a **Video Capture Device** and choose `WindowsCam`. Use 4K40 in OBS by selecting the 3840x2160 40fps camera format when available.

## Helper tools

The receiver needs:

- `iproxy.exe`
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
  "codec": "nv12-raw",
  "pixelFormat": "nv12",
  "width": 3840,
  "height": 2160,
  "fps": 40,
  "lumaStride": 3840,
  "bytesPerFrame": 12441600,
  "bytesPerSecond": 497664000,
  "frameHeaderBytes": 48,
  "orientation": "landscapeRight",
  "framing": "uint32be-length-prefixed/raw-v2"
}
```

All following packets contain a 48-byte raw-v2 frame header followed by one packed NV12 payload. The Windows receiver still accepts legacy raw packets that contain only the NV12 payload, but the iOS app sends raw-v2 so dimensions and fps cannot drift.
