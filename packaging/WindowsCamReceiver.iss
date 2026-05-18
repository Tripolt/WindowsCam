#define AppName "WindowsCam Receiver"
#define AppVersion "0.1.0"
#define AppPublisher "Matteo Tripolt"
#define AppExeName "WindowsCamReceiver.exe"

[Setup]
AppId={{1FA3FD75-95A7-4DA4-8AA3-6AC143F4B8F7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\WindowsCam Receiver
DefaultGroupName=WindowsCam Receiver
DisableProgramGroupPage=yes
OutputDir=dist\installer
OutputBaseFilename=WindowsCamReceiverSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "dist\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WindowsCam Receiver"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Install Guide"; Filename: "{app}\WINDOWS_INSTALL.md"
Name: "{autodesktop}\WindowsCam Receiver"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch WindowsCam Receiver"; Flags: nowait postinstall skipifsilent
