[CmdletBinding()]
param([string]$PaneSource, [string]$HarnessSource)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio MSBuild and the .NET Framework 4.8 targeting pack are required.' }
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsRoot)) { throw 'Visual Studio MSBuild was not found.' }
$msbuild = Join-Path $vsRoot.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
$compiler = Join-Path $vsRoot.Trim() 'MSBuild\Current\Bin\Roslyn\csc.exe'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))) { throw 'The .NET Framework 4.8 targeting pack was not found.' }

# Restores Newtonsoft.Json through the ordinary class library. This target does
# not build/register the VSTO project, start Excel, or contact a model provider.
& $msbuild (Join-Path $workspace 'src\Aperture.Runtime\Aperture.Runtime.csproj') /restore /t:Build /nologo /v:minimal /p:Configuration=Debug /p:Platform=AnyCPU
if ($LASTEXITCODE -ne 0) { throw 'The runtime dependency build failed.' }
$json = Join-Path $workspace 'src\Aperture.Runtime\bin\Debug\net48\Newtonsoft.Json.dll'
$artifactDirectory = Join-Path $workspace '.local\tests\vsto'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$executable = Join-Path $artifactDirectory 'PaneLifecycleHarness.exe'
$arguments = @('/nologo', '/target:exe', '/langversion:7.3', '/nostdlib+', "/out:$executable")
foreach ($assembly in @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms')) {
    $arguments += '/reference:' + (Join-Path $framework ($assembly + '.dll'))
}
$defaultSource = [string]::IsNullOrWhiteSpace($PaneSource)
if ($defaultSource) { $PaneSource = Join-Path $workspace 'src\Aperture.Excel\ChatPaneControl.cs' }
if ([string]::IsNullOrWhiteSpace($HarnessSource)) { $HarnessSource = Join-Path $workspace 'tests\vsto\PaneLifecycleHarness.cs' }
$arguments += @("/reference:$json", $PaneSource,
    (Join-Path $workspace 'tests\vsto\FakeDependencies.cs'), $HarnessSource)
# The production ChatPaneView/WebView2 is replaced by the thread-checked fake
# view; compile the real controller only, with no browser installation required.
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Pane lifecycle harness compilation failed.' }
Copy-Item -LiteralPath $json -Destination (Join-Path $artifactDirectory 'Newtonsoft.Json.dll') -Force
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Pane lifecycle regression tests failed.' }
