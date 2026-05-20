param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller,
    [switch]$IncludeLocalTools
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectPath = Join-Path $RepoRoot "WindowsCamReceiver\WindowsCamReceiver.csproj"
$VirtualCameraToolProject = Join-Path $RepoRoot "WindowsCam.VirtualCamera.Tool\WindowsCam.VirtualCamera.Tool.vcxproj"
$PublishDir = Join-Path $RepoRoot "packaging\dist\publish\$Runtime"
$InstallerScript = Join-Path $RepoRoot "packaging\WindowsCamReceiver.iss"
$ToolsSource = Join-Path $RepoRoot "WindowsCamReceiver\Tools"
$ToolsDest = Join-Path $PublishDir "Tools"

function Require-Command {
    param([string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "$Name was not found. Install it and run this script again."
    }

    return $command.Source
}

function Copy-ToolFromPath {
    param([string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        New-Item -ItemType Directory -Force -Path $ToolsDest | Out-Null
        Copy-Item -Force -Path $command.Source -Destination (Join-Path $ToolsDest (Split-Path $command.Source -Leaf))
        Write-Host "Bundled $Name from PATH."
    }
    else {
        Write-Host "$Name not found on PATH; not bundled."
    }
}

Require-Command "dotnet" | Out-Null

if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}

New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

dotnet publish $ProjectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $PublishDir

$msbuild = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
if ($msbuild -and (Test-Path $VirtualCameraToolProject)) {
    & $msbuild.Source $VirtualCameraToolProject /p:Configuration=Release /p:Platform=x64 /m
    $toolOutput = Join-Path $RepoRoot "WindowsCam.VirtualCamera.Tool\x64\Release\WindowsCam.VirtualCamera.Tool.exe"
    if (Test-Path $toolOutput) {
        Copy-Item -Force -Path $toolOutput -Destination $PublishDir
    }
}
else {
    Write-Host "MSBuild.exe was not found; native virtual camera tool was not built."
}

Copy-Item -Force -Path (Join-Path $RepoRoot "WindowsCamReceiver\WINDOWS_INSTALL.md") -Destination $PublishDir
Copy-Item -Force -Path (Join-Path $RepoRoot "README.md") -Destination $PublishDir

if (Test-Path $ToolsSource) {
    New-Item -ItemType Directory -Force -Path $ToolsDest | Out-Null
    Get-ChildItem $ToolsSource -File | Where-Object {
        $_.Extension -in @(".exe", ".dll") -or $_.Name -eq "README.md"
    } | ForEach-Object {
        Copy-Item -Force -Path $_.FullName -Destination $ToolsDest
    }
}

if ($IncludeLocalTools) {
    Copy-ToolFromPath "ffmpeg.exe"
    Copy-ToolFromPath "iproxy.exe"
    Copy-ToolFromPath "idevice_id.exe"
    Copy-ToolFromPath "idevicename.exe"
}

if ($SkipInstaller) {
    Write-Host "Published app to $PublishDir"
    return
}

$isccPath = $null
$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($iscc) {
    $isccPath = $iscc.Source
}
else {
    $defaultIscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $defaultIscc) {
        $isccPath = $defaultIscc
    }
}

if (-not $isccPath) {
    Write-Host "Inno Setup was not found. Install Inno Setup 6 or rerun with -SkipInstaller."
    Write-Host "Published app is ready at $PublishDir"
    return
}

Push-Location (Join-Path $RepoRoot "packaging")
try {
    & $isccPath $InstallerScript
}
finally {
    Pop-Location
}
Write-Host "Installer output: $(Join-Path $RepoRoot 'packaging\dist\installer')"
