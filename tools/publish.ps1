<#
.SYNOPSIS
    Publishes RecentDock to dist\ and code-signs the result.

.DESCRIPTION
    Publish and sign are one step on purpose: a rebuild produces a brand-new, unsigned
    binary, and SmartScreen blocks unsigned binaries it has never seen. Publishing
    without signing guarantees that the next launch is interrupted by a warning, so the
    two are kept together rather than left to be remembered.

    Signing needs the local certificate from tools\sign.ps1, which creates it on first
    use.

.PARAMETER SkipSign
    Publish only. The output will be unsigned, so Windows may warn on first launch.
#>
[CmdletBinding()]
param(
    [switch] $SkipSign,
    [string] $Output = 'dist'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RecentDock.App\RecentDock.App.csproj'
$outputDirectory = Join-Path $root $Output

Write-Host "RecentDock publish"
Write-Host "  project : $project"
Write-Host "  output  : $outputDirectory"

# -m:1 and -nodeReuse:false keep MSBuild from spinning up worker nodes over named pipes,
# which sandboxed shells refuse. The cost is a slightly slower build; the benefit is that
# this script works in the same environment the rest of the tooling does.
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $outputDirectory -m:1 -nodeReuse:false

if ($LASTEXITCODE -ne 0) {
    Write-Host "publish failed ($LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

if ($SkipSign) {
    Write-Host "skipping the signature (the next launch may show a SmartScreen warning)" -ForegroundColor Yellow
    exit 0
}

& (Join-Path $PSScriptRoot 'sign.ps1') -Exe (Join-Path $outputDirectory 'RecentDock.exe')
exit $LASTEXITCODE
