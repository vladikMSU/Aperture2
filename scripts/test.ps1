[CmdletBinding()]
param([switch]$Live)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
$previousPythonPath = $env:PYTHONPATH
Push-Location $workspace
try {
    $env:PYTHONPATH = Join-Path $workspace 'src'
    & .\.venv\Scripts\python.exe -m pytest tests\backend -q
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
    $piSmoke = Join-Path $workspace 'tools\PiSmoke\bin\Debug\net48\PiSmoke.exe'
    if (-not (Test-Path -LiteralPath $piSmoke)) { throw 'Run scripts\build.ps1 first (Debug).' }
    & $piSmoke --self-test --root $workspace
    if ($LASTEXITCODE -ne 0) { throw 'Runtime tests failed.' }
    & (Join-Path $PSScriptRoot 'test-vsto.ps1')
    if ($Live) {
        Write-Host 'Opening a new disposable Excel process and unsaved test books; existing books are not targets.'
        & .\.venv\Scripts\python.exe tools\LocalSmoke\run.py
        if ($LASTEXITCODE -ne 0) { throw 'Live integration smoke failed; see .local\smoke reports.' }
    }
} finally {
    $env:PYTHONPATH = $previousPythonPath
    Pop-Location
}
