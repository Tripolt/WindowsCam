# WindowsCam Virtual Camera Source

This folder defines the native Media Foundation source boundary used by the Windows 11 virtual camera registration.

## CLSID

`{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}`

The registration tool passes this CLSID to `MFCreateVirtualCamera`. The matching COM DLL must implement a Frame Server Custom Media Source under this CLSID.

## Frame Broker Contract

The receiver publishes the latest decoded frame to:

```text
%ProgramData%\WindowsCam\latest-frame.mmf
```

The file is a memory-mapped frame buffer with a 64-byte little-endian header followed by one NV12 frame.

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | UInt64 | Magic `WCAMFRAM` (`0x5743414D4652414D`) |
| 8 | Int32 | Header version, currently `1` |
| 12 | Int32 | Width |
| 16 | Int32 | Height |
| 20 | Int32 | FPS |
| 24 | Int32 | Luma stride, currently equal to width |
| 28 | Int32 | Payload byte count |
| 32 | Int64 | Monotonic frame sequence |
| 40 | Int64 | UTC timestamp in Unix milliseconds |
| 48 | 16 bytes | Reserved |

Supported modes for v1:

- `3840x2160 30fps` NV12
- `1920x1080 30fps` NV12
- `1280x720 30fps` NV12

The custom source should expose those three media types and, on each `IMFMediaStream::RequestSample`, copy the newest payload into an `IMFSample` allocated with Media Foundation buffer APIs. If no fresh iPhone frame is available, it should repeat the newest frame or emit a neutral placeholder so Teams, Zoom, browsers, and OBS keep the camera alive.

## Implementation Notes

Frame Server loads the source as Local Service in Session 0, so the source must not depend on the receiver process UI, window messages, or user-profile paths. The `%ProgramData%` broker path is used deliberately so both the receiver and Frame Server source can open the same data.
