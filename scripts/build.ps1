[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$CertificateThumbprint,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with Office/VSTO development tools is required.' }
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsRoot)) { throw 'Visual Studio MSBuild was not found.' }
$msbuild = Join-Path $vsRoot.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
$officeTargets = Join-Path $vsRoot.Trim() 'MSBuild\Microsoft\VisualStudio\v17.0\OfficeTools\Microsoft.VisualStudio.Tools.Office.targets'
if (-not (Test-Path -LiteralPath $officeTargets)) { throw 'Install the Visual Studio Office/SharePoint development workload.' }

if ($Configuration -eq 'Release' -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'Release requires an explicit signing CertificateThumbprint. Use Debug for local development.'
}
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq 'CN=Aperture2 Development' -and $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(1) -and
        @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($null -eq $certificate) {
        Write-Host 'Creating the local non-exportable Aperture2 development signing certificate.'
        $certificate = New-SelfSignedCertificate -Subject 'CN=Aperture2 Development' -Type CodeSigningCert `
            -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(2) `
            -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable
    }
    $CertificateThumbprint = $certificate.Thumbprint
} else {
    $CertificateThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($CertificateThumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Invalid certificate thumbprint.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or
        @($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
        throw 'The certificate must have an accessible private key, a code-signing EKU and be unexpired.'
    }
    if ($Configuration -eq 'Release' -and $certificate.Subject -eq 'CN=Aperture2 Development') {
        throw 'The local development certificate cannot sign Release builds.'
    }
}

$project = Join-Path $workspace 'src\Aperture.Excel\Aperture.Excel.csproj'
$target = if ($Rebuild) { 'Rebuild' } else { 'Build' }
# Office targets register this add-in and its manifest inclusion entry for the current user.
# They do not add a certificate to Trusted Root or change general Office trust settings.
Write-Host 'Building and registering the Aperture.Excel development add-in for the current user.'
& $msbuild $project /restore "/t:$target" /m /nologo /v:minimal `
    "/p:Configuration=$Configuration" /p:Platform=AnyCPU /p:SignManifests=true `
    "/p:ManifestCertificateThumbprint=$CertificateThumbprint"
if ($LASTEXITCODE -ne 0) { throw "VSTO build failed: $LASTEXITCODE" }
& $msbuild (Join-Path $workspace 'tools\PiSmoke\PiSmoke.csproj') /restore "/t:$target" /m /nologo /v:minimal `
    "/p:Configuration=$Configuration" /p:Platform=AnyCPU
if ($LASTEXITCODE -ne 0) { throw "PiSmoke build failed: $LASTEXITCODE" }
Write-Host "Built: $(Join-Path $workspace "src\Aperture.Excel\bin\$Configuration\Aperture.Excel.vsto")"
