# Optional Helper Tools

Place Windows helper binaries here before running `packaging/Build-Installer.ps1` if you want them bundled into the installer.

Expected filenames:

- `iproxy.exe`
- `idevice_id.exe`
- `idevicename.exe`

Only `iproxy.exe` is required for the raw video link. `idevice_id.exe` and `idevicename.exe` are optional diagnostics. The receiver also searches the user's `PATH`, so bundling is convenient but not mandatory.
