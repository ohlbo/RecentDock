<#
.SYNOPSIS
    Installs the published build into the user's profile and starts it from there.

.DESCRIPTION
    Why this exists: running the executable straight out of the build output does not
    work in every environment. On this machine, anything under the agent workspace is
    classified by the shell as coming from the Internet: double-clicking
    dist\RecentDock.exe raises "打开文件 - 安全警告" ("来自 Internet 的文件") even though
    the file carries no Mark of the Web, while the byte-identical copy on the Desktop
    starts silently. The location is the trigger, not the binary.

    Installing to %LOCALAPPDATA%\RecentDock\App also happens to be the right place for a
    tray utility: the build output is disposable, the installed copy is what the
    shortcut, the autostart entry and the snapshot all refer to.

.PARAMETER Source
    Folder holding the published build. Defaults to dist\ in the repository root.

.PARAMETER NoShortcut
    Skip creating the desktop and Start Menu shortcuts.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host "  $text" }

$installDirectory = Join-Path $env:LOCALAPPDATA 'RecentDock\App'
$exe = Join-Path $installDirectory 'RecentDock.exe'

Write-Host "RecentDock local install"
Write-Step "source : $Source"
Write-Step "target : $installDirectory"

if (-not (Test-Path (Join-Path $Source 'RecentDock.exe'))) {
    Write-Host "FAILED: no RecentDock.exe in $Source - run tools\publish.ps1 first" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null

# Only the runtime files: the .pdb files belong to the build output, not to an install.
foreach ($file in Get-ChildItem $Source -File) {
    if ($file.Extension -eq '.pdb') { continue }
    Copy-Item $file.FullName -Destination $installDirectory -Force
}

Write-Step "copied $((Get-ChildItem $installDirectory -File).Count) file(s)"

# Keep the "built here" marking; the copy carries the signature already.
try {
    Set-Content -LiteralPath $exe -Stream 'Zone.Identifier' `
        -Value "[ZoneTransfer]`r`nZoneId=0" -Encoding Ascii -NoNewline -ErrorAction Stop
}
catch {
    Write-Step "note: could not mark the copy as local ($($_.Exception.Message))"
}

$signature = Get-AuthenticodeSignature $exe
Write-Step "signature : $($signature.Status)"

# ---------------------------------------------------------------- shortcuts

function New-Shortcut([string] $linkPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path -Parent $exe
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'RecentDock - 最近项目面板'
    $shortcut.Save()
}

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'RecentDock.lnk'

    New-Shortcut (Join-Path $desktop 'RecentDock.lnk')
    Write-Step "shortcut  : $(Join-Path $desktop 'RecentDock.lnk')"

    New-Shortcut $startMenu
    Write-Step "shortcut  : $startMenu"
}

# The autostart entry must point at the installed copy, not at the build output.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty $runKey -ErrorAction SilentlyContinue).RecentDock) {
    Set-ItemProperty $runKey -Name 'RecentDock' -Value "`"$exe`"" -Type String
    Write-Step "autostart : repointed to the installed copy"
}

Write-Host ""
Write-Host "Installed. Launch it with the RecentDock shortcut, or:" -ForegroundColor Green
Write-Host "  $exe"
