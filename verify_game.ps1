<#
.SYNOPSIS
    Game Verification Script
.DESCRIPTION
    Builds both Content.Server and Content.Client to ensure the game is in a playable state.
#>

$ErrorActionPreference = "Stop"

Write-Host "Building Content.Server..." -ForegroundColor Cyan
dotnet build Content.Server
if ($LASTEXITCODE -ne 0) { Write-Error "Server build failed!"; exit 1 }

Write-Host "Building Content.Client..." -ForegroundColor Cyan
dotnet build Content.Client
if ($LASTEXITCODE -ne 0) { Write-Error "Client build failed!"; exit 1 }

Write-Host "Game (Server + Client) Verified Successfully!" -ForegroundColor Green
