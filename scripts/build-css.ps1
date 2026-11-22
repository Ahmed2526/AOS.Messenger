<#
.SYNOPSIS
    Builds static CSS for AOS.Messenger using pinned Tailwind CSS v4 standalone CLI.
#>
param(
    [string]$Version = "v4.3.5"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path "$scriptDir/..").Path
Set-Location $repoRoot

$toolsDir = Join-Path $repoRoot ".tools"
if (-not (Test-Path $toolsDir)) {
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
}

$tailwindExe = Join-Path $toolsDir "tailwindcss.exe"
if (-not (Test-Path $tailwindExe)) {
    Write-Host "Downloading Tailwind CSS standalone CLI ($Version)..."
    $downloadUrl = "https://github.com/tailwindlabs/tailwindcss/releases/download/$Version/tailwindcss-windows-x64.exe"
    
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -sL -o $tailwindExe $downloadUrl
    } else {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tailwindExe
    }
}

# Also ensure a non-extension copy exists for cross-shell invocation
$tailwindNoExt = Join-Path $toolsDir "tailwindcss"
if (-not (Test-Path $tailwindNoExt)) {
    Copy-Item $tailwindExe $tailwindNoExt -Force
}

Write-Host "Compiling Tailwind CSS..."
& $tailwindExe -i src/AOS.Messenger.Web/Styles/app.css -o src/AOS.Messenger.Web/wwwroot/css/app.css --minify
Write-Host "Tailwind CSS compilation complete: src/AOS.Messenger.Web/wwwroot/css/app.css"
