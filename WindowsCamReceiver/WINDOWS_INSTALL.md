# WindowsCam Receiver Install Guide

## Quick Start

1. Install the WindowsCam Receiver installer.
2. Install OBS Studio if it is not installed already.
3. Make sure the helper tools are available:
   - `ffmpeg.exe`
   - `iproxy.exe`
   - optional: `idevice_id.exe` and `idevicename.exe`
4. Put helper tools in either:
   - `C:\Program Files\WindowsCam Receiver\Tools`
   - any folder already on your `PATH`
5. Open the iPhone app, keep it running, and connect the iPhone by cable.
6. Start WindowsCam Receiver.
7. In OBS, add a Media Source with this input:

```text
udp://127.0.0.1:48651
```

## iPhone Trust

If the receiver cannot find the iPhone, unlock the iPhone, unplug and reconnect the cable, then accept the Trust This Computer prompt.

## Tools

`ffmpeg.exe` publishes the incoming H.264 stream to OBS.

`iproxy.exe` forwards the iPhone app's USB-muxed TCP port to Windows localhost. It is normally provided by libimobiledevice builds for Windows.

The installer can bundle these tools if they are placed in `WindowsCamReceiver\Tools` before packaging.
