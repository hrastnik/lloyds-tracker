# Publishes a small framework-dependent single-file LloydsTracker.exe into windows/dist.
# Building requires the .NET 8 SDK; running requires the .NET 8 Desktop Runtime
# (winget install Microsoft.DotNet.DesktopRuntime.8). See https://dotnet.microsoft.com/download.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "-> dotnet publish -c Release" -ForegroundColor Cyan
dotnet publish LloydsTracker/LloydsTracker.csproj -c Release -o dist

Write-Host ""
Write-Host "Done: $PSScriptRoot\dist\LloydsTracker.exe" -ForegroundColor Green
Write-Host ""
Write-Host "Run it:            .\dist\LloydsTracker.exe"
Write-Host "Autostart (opt.):  enable 'Pokreni kod prijave' in Postavke, or copy a shortcut to shell:startup"
