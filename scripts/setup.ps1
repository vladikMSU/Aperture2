[CmdletBinding()]
param([switch]$SkipPi, [switch]$SkipPython)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    & git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw 'Submodule checkout failed.' }
    if (-not $SkipPython) {
        if (-not (Test-Path -LiteralPath '.venv\Scripts\python.exe')) {
            & python -m venv .venv
            if ($LASTEXITCODE -ne 0) { throw 'Python 3.11+ with venv is required (tested with 3.12).' }
        }
        & .\.venv\Scripts\python.exe -m pip install -r requirements.lock.txt
        if ($LASTEXITCODE -ne 0) { throw 'Python dependency installation failed.' }
        & .\.venv\Scripts\python.exe -m pip install --no-deps -e .\external\thepexcel
        if ($LASTEXITCODE -ne 0) { throw 'ThepExcelMCP installation failed.' }
        & .\.venv\Scripts\python.exe -m pip check
        if ($LASTEXITCODE -ne 0) { throw 'Python dependencies are inconsistent.' }
    }
    if (-not $SkipPi) {
        $nodeVersionText = & node --version
        if ($LASTEXITCODE -ne 0 -or [version]($nodeVersionText.TrimStart('v')) -lt [version]'22.19.0') {
            throw 'Node.js >=22.19 is required to build Pi.'
        }
        Push-Location external\pi
        try {
            & npm.cmd ci --ignore-scripts --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw 'Pi npm ci failed.' }
            & npm.cmd run build
            if ($LASTEXITCODE -ne 0) { throw 'Pi build failed.' }
        } finally { Pop-Location }
    }
    Write-Host 'Dependencies ready. Run scripts\build.ps1 to build and register the VSTO add-in.'
} finally { Pop-Location }
