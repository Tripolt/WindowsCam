#define AppName "WindowsCam"
#define AppVersion "0.2.0"
#define AppPublisher "Matteo Tripolt"
#define AppExeName "WindowsCamReceiver.exe"

[Setup]
AppId={{1FA3FD75-95A7-4DA4-8AA3-6AC143F4B8F7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\WindowsCam
DefaultGroupName=WindowsCam
DisableProgramGroupPage=yes
OutputDir=dist\installer
OutputBaseFilename=WindowsCamReceiverSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "dist\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WindowsCam"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Install Guide"; Filename: "{app}\WINDOWS_INSTALL.md"
Name: "{autodesktop}\WindowsCam"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\WindowsCam.VirtualCamera.Tool.exe"; Parameters: "register"; StatusMsg: "Registering WindowsCam virtual camera..."; Flags: runhidden skipifdoesntexist
Filename: "{app}\{#AppExeName}"; Description: "Launch WindowsCam"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\WindowsCam.VirtualCamera.Tool.exe"; Parameters: "remove"; Flags: runhidden skipifdoesntexist
