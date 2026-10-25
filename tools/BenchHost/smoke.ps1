[CmdletBinding()]
param(
    [string]$Provider = 'routerai',
    [string]$Model = 'deepseek/deepseek-v4.1-flash',
    [string]$ApiKeyEnv = 'ROUTERAI_API_KEY',
    [string]$CostCurrency = 'RUB',
    [string]$Thinking,
    [switch]$SkipTimeout
)
# BenchHost acceptance smoke: a new disposable Excel with its own book, a real model request,
# then a --timeout-s 20 run. Needs a Debug build of BenchHost and the provider's key variable.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$python = Join-Path $workspace '.venv\Scripts\python.exe'
$benchHost = Join-Path $workspace 'tools\BenchHost\bin\Debug\net48\BenchHost.exe'
foreach ($required in @($python, $benchHost)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing $required. Build Aperture2.sln (Debug) in Visual Studio first." }
}
$arguments = @((Join-Path $PSScriptRoot 'smoke.py'), '--provider', $Provider, '--model', $Model, '--api-key-env', $ApiKeyEnv, '--cost-currency', $CostCurrency)
if ($Thinking) { $arguments += @('--thinking', $Thinking) }
if ($SkipTimeout) { $arguments += '--skip-timeout' }
Write-Host 'Opening a new disposable Excel process and its own test book; existing books are not targets.'
& $python @arguments
exit $LASTEXITCODE
