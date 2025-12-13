<#
.SYNOPSIS
    Agent Environment Initialization Script
.DESCRIPTION
    Sets up and verifies the development environment for Space Station 14.
    1. Checks for required tools (dotnet, python).
    2. Runs the repo's internal setup script (RUN_THIS.py).
    3. Builds the solution to verify the environment is healthy.
#>

$ErrorActionPreference = "Stop"

function Write-Status {
    param([string]$Message)
    Write-Host "[AGENT-INIT] $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[SUCCESS] $Message" -ForegroundColor Green
}

function Write-ErrorMsg {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

try {
    # 1. Check Dependencies
    Write-Status "Checking dependencies..."

    if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
        throw "dotnet SDK is not installed or not in PATH."
    }
    Write-Host "  - dotnet found"

    # Check for python (could be python or python3)
    if (Get-Command "python" -ErrorAction SilentlyContinue) {
        $pythonCmd = "python"
    } elseif (Get-Command "python3" -ErrorAction SilentlyContinue) {
        $pythonCmd = "python3"
    } else {
        throw "Python is not installed or not in PATH."
    }
    Write-Host "  - python found ($pythonCmd)"

    # 2. Run Repository Setup
    Write-Status "Running repository setup (RUN_THIS.py)..."
    if (Test-Path "RUN_THIS.py") {
        & $pythonCmd RUN_THIS.py
        if ($LASTEXITCODE -ne 0) { throw "RUN_THIS.py failed." }
    } else {
        throw "RUN_THIS.py not found in current directory."
    }

    # 3. Build Solution
    Write-Status "Building solution (SpaceStation14.sln)..."
    dotnet build SpaceStation14.sln
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    Write-Success "Environment initialization complete! The solution built successfully."

} catch {
    Write-ErrorMsg $_.Exception.Message
    exit 1
}
