# Optional Helper Tools

Place Windows helper binaries here before running `packaging/Build-Installer.ps1` if you want them bundled into the installer.

Expected filenames:

- `ffmpeg.exe`
- `iproxy.exe`
- `idevice_id.exe`
- `idevicename.exe`

The receiver also searches the user's `PATH`, so bundling is convenient but not mandatory.
