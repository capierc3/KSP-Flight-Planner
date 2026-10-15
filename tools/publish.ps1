# Publishes the standalone exe into a folder per build, so a branch build never overwrites the main one:
#   tagged release v1.0.0  -> publish\v1.0.0\KspFlightPlanner.exe
#   main / develop         -> publish\main\..., publish\develop\...
#   feature branch         -> publish\<branch>\...  (slashes become dashes)
# Usage: pwsh tools/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'src/KspFlightPlanner.App'

# "1.0.2-SNAPSHOT+fly-this-mission.0dc323a [snapshot]", from Directory.Build.targets.
$line = (dotnet msbuild -t:PrintVersion -nologo $app | Select-Object -Last 1).Trim()
if ($line -notmatch '^(?<version>[^+\s]+)\S*\s+\[(?<channel>\w+)\]$') { throw "Couldn't read the build version: $line" }
$version = $Matches.version
$channel = $Matches.channel

$folder = if ($channel -eq 'release') { "v$version" } else {
    (git -C $root rev-parse --abbrev-ref HEAD).Trim() -replace '[^0-9A-Za-z-]+', '-'
}
$out = Join-Path $root "publish/$folder"

dotnet publish $app -c Release -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nKSP Flight Planner $version ($channel) -> $out\KspFlightPlanner.exe"
