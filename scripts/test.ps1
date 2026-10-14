[CmdletBinding()]
param([switch]$Live)
# Runs the checks against an existing Debug build of Aperture2.sln (build it in Visual Studio first).
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
$previousPythonPath = $env:PYTHONPATH
Push-Location $workspace
try {
    $python = Join-Path $workspace '.venv\Scripts\python.exe'
    $piSmoke = Join-Path $workspace 'tools\PiSmoke\bin\Debug\net48\PiSmoke.exe'
    $paneHarness = Join-Path $workspace 'tests\vsto\bin\Debug\net48\PaneLifecycleHarness.exe'
    foreach ($required in @($python, $piSmoke, $paneHarness)) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Missing $required. Build Aperture2.sln (Debug) in Visual Studio first." }
    }
    $env:PYTHONPATH = Join-Path $workspace 'src'
    & $python -m pytest tests\backend -q
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
    & $piSmoke --self-test --root $workspace
    if ($LASTEXITCODE -ne 0) { throw 'Runtime tests failed.' }
    & $paneHarness
    if ($LASTEXITCODE -ne 0) { throw 'Pane lifecycle regression tests failed.' }
    $playwright = & node -e "try { require.resolve('playwright'); process.stdout.write('yes') } catch { process.stdout.write('no') }"
    if ($playwright -eq 'yes') {
        & node tests\web\chat-view.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Chat UI browser tests failed.' }
    } else {
        Write-Host 'Chat UI browser test skipped: Playwright was not found (set NODE_PATH, see tests\web\README.md).'
    }
    if ($Live) {
        Write-Host 'Opening a new disposable Excel process and unsaved test books; existing books are not targets.'
        & $python tools\LocalSmoke\run.py
        if ($LASTEXITCODE -ne 0) { throw 'Live integration smoke failed; see .local\smoke reports.' }
    }
} finally {
    $env:PYTHONPATH = $previousPythonPath
    Pop-Location
}
