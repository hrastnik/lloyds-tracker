# Publishes a trimmed, self-contained single-file LloydsTracker.exe into windows/dist.
# Requires the .NET 8 SDK on Windows (https://dotnet.microsoft.com/download).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "-> dotnet publish -c Release" -ForegroundColor Cyan
dotnet publish LloydsTracker/LloydsTracker.csproj -c Release -o dist

Write-Host ""
Write-Host "Done: $PSScriptRoot\dist\LloydsTracker.exe" -ForegroundColor Green
Write-Host ""
Write-Host "Run it:            .\dist\LloydsTracker.exe"
Write-Host "Autostart (opt.):  enable 'Pokreni kod prijave' in Postavke, or copy a shortcut to shell:startup"
