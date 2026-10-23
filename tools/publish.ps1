# Publishes the standalone exe into a folder per branch, so a branch build never overwrites the main one:
#   main / develop  -> publish\main\..., publish\develop\...
#   feature branch  -> publish\<branch>\...  (slashes become dashes)
# Usage: pwsh tools/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'src/KspFlightPlanner.App'

$version = (dotnet msbuild $app -getProperty:Version).Trim() # from Directory.Build.props
$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim() -replace '[^0-9A-Za-z-]+', '-'
$out = Join-Path $root "publish/$branch"

dotnet publish $app -c Release -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nKSP Flight Planner $version ($branch) -> $out\KspFlightPlanner.exe"
