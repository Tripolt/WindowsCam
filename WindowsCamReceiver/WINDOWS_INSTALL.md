# WindowsCam Install Guide

## Quick Start

1. Install WindowsCam as administrator.
2. Make sure helper tools are available:
   - `iproxy.exe`
   - optional: `idevice_id.exe` and `idevicename.exe`
3. Put helper tools in either:
   - `C:\Program Files\WindowsCam\Tools`
   - any folder already on your `PATH`
4. Open the iPhone app, keep it running, and connect the iPhone by cable.
5. Open WindowsCam; it starts and reconnects automatically.
6. Select `WindowsCam` in Teams, Zoom, browsers, or OBS.

In OBS, add **Video Capture Device** and choose `WindowsCam`. Do not add a Media Source URL.

## 4K in OBS

The virtual camera target formats are:

- `3840x2160 30fps`
- `3840x2160 40fps`
- `1920x1080 60fps`
- `1920x1080 40fps`
- `1920x1080 30fps`
- `1280x720 60fps`
- `1280x720 40fps`
- `1280x720 30fps`

Teams and Zoom usually prefer 1080p or lower. OBS can request the 4K format from the Video Capture Device properties when the native Media Foundation source is installed.

## iPhone Trust

If the receiver cannot find the iPhone, unlock the iPhone, unplug and reconnect the cable, then accept the Trust This Computer prompt.

## Tools

`iproxy.exe` forwards the iPhone app's USB-muxed TCP port to Windows localhost. It is normally provided by libimobiledevice builds for Windows.

WindowsCam is raw NV12 end to end. There is no H.264 or ffmpeg fallback in the active path.

## Native Camera Component

WindowsCam uses the Windows 11 Media Foundation virtual camera API. The installer must bundle:

- `WindowsCamReceiver.exe`
- `WindowsCam.VirtualCamera.Tool.exe`
- the native Custom Media Source COM DLL for CLSID `{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}`

The receiver writes the latest raw frame to:

```text
%ProgramData%\WindowsCam\latest-frame.mmf
```

The Custom Media Source reads that frame and serves it to Windows camera clients.
