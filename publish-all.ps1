#!/usr/bin/env pwsh
# Publishes self-contained single-file binaries for all six supported targets.
# Output is written to publish/ under the project root as <rid>-invitebot[.exe].
# Usage: pwsh publish-all.ps1

$ErrorActionPreference = "Stop"

$rids = @(
    "win-x64",
    "win-arm64",
    "osx-x64",
    "osx-arm64",
    "linux-x64",
    "linux-arm64"
)

if (Test-Path "publish")
{
    Write-Host "Cleaning existing publish/ directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "publish"
}

New-Item -ItemType Directory -Path "publish" | Out-Null

foreach ($rid in $rids)
{
    $tempDir = "publish/_tmp_$rid"
    Write-Host "`nPublishing $rid..." -ForegroundColor Cyan
    dotnet publish invitebot.csproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $tempDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $ext = if ($rid.StartsWith("win")) { ".exe" } else { "" }
    $src = Join-Path $tempDir "invitebot$ext"
    $dst = Join-Path "publish" "$rid-invitebot$ext"
    Move-Item -Force $src $dst
    Remove-Item -Recurse -Force $tempDir
}

Write-Host "`nDone. Binaries written to publish/:" -ForegroundColor Green
foreach ($rid in $rids)
{
    $ext = if ($rid.StartsWith("win")) { ".exe" } else { "" }
    Write-Host "  publish/$rid-invitebot$ext"
}
