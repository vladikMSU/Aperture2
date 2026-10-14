[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsRoot)) { throw 'Visual Studio MSBuild was not found.' }
$msbuild = Join-Path $vsRoot.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $workspace 'src\Aperture.Excel\Aperture.Excel.csproj') `
    '/t:UnregisterOfficeAddin;RemoveOfficeAddInSecurity' /nologo /v:minimal `
    "/p:Configuration=$Configuration" /p:Platform=AnyCPU
if ($LASTEXITCODE -ne 0) { throw "Unregister failed: $LASTEXITCODE" }
Write-Host 'Aperture.Excel development registration removed. Project files and local Pi history retained.'
