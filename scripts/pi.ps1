# Run the pinned Pi CLI interactively, for example to enter /login openrouter.
# The API key is handled by Pi's auth store and is not saved in this repository.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $workspace 'external\pi\packages\coding-agent\dist\bundle\cli.js'
if (-not (Test-Path -LiteralPath $cli)) { throw 'Run scripts\setup.ps1 first.' }
& node $cli @args
exit $LASTEXITCODE
