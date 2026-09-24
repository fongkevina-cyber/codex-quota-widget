[CmdletBinding()]
param(
    [string] $ArtifactPath,

    [switch] $LaunchSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory)]
        [object] $Object,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "Manifest property '$Name' is missing."
    }

    return $property.Value
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a PE executable (missing MZ header)."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "'$Path' has an invalid PE header offset."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' is not a PE executable (missing PE signature)."
        }

        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) {
    $ArtifactPath = Join-Path $projectRoot 'artifacts\publish\win-x64'
}
$ArtifactPath = Get-NormalizedFullPath -Path $ArtifactPath
$expectedPackageFamilyName = 'OpenAI.Codex_2p2nqsd0c76g0'
$expectedPublisherId = '2p2nqsd0c76g0'
$expectedSignatureKind = 'Store'
$ownedArtifactFiles = @(
    'CodexQuotaWidget.exe',
    'runtime\codex.exe',
    'runtime-manifest.json',
    'Uninstall.ps1',
    'Common.ps1'
)

if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Container)) {
    throw "Artifact directory not found: '$ArtifactPath'."
}
[void] (Assert-NoReparsePointInPath -Path $ArtifactPath)
[void] (Assert-WidgetOwnedDirectory `
    -Directory $ArtifactPath `
    -RequiredRelativePaths $ownedArtifactFiles)
[void] (Assert-NoReparsePointInDirectoryTree -Directory $ArtifactPath)

$manifestPath = Join-Path $ArtifactPath 'runtime-manifest.json'
$appPath = Join-Path $ArtifactPath 'CodexQuotaWidget.exe'
$codexPath = Join-Path $ArtifactPath 'runtime\codex.exe'

foreach ($requiredPath in @(
    $manifestPath,
    $appPath,
    $codexPath,
    (Join-Path $ArtifactPath 'CodexQuotaWidget.runtimeconfig.json'),
    (Join-Path $ArtifactPath 'hostfxr.dll'),
    (Join-Path $ArtifactPath 'hostpolicy.dll'),
    (Join-Path $ArtifactPath 'coreclr.dll'),
    (Join-Path $ArtifactPath 'Uninstall.ps1'),
    (Join-Path $ArtifactPath 'Common.ps1')
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required artifact file is missing: '$requiredPath'."
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int] (Get-RequiredPropertyValue -Object $manifest -Name 'schemaVersion') -ne 1) {
    throw 'Unsupported runtime manifest schema version.'
}

$application = Get-RequiredPropertyValue -Object $manifest -Name 'application'
$codex = Get-RequiredPropertyValue -Object $manifest -Name 'codex'
$authenticode = Get-RequiredPropertyValue -Object $codex -Name 'authenticode'

$generatedAtText = [string] (Get-RequiredPropertyValue -Object $manifest -Name 'generatedAtUtc')
$generatedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse(
        $generatedAtText,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref] $generatedAt)) {
    throw "Manifest generatedAtUtc is not a valid round-trip timestamp: '$generatedAtText'."
}

if ((Get-RequiredPropertyValue -Object $application -Name 'name') -cne 'CodexQuotaWidget' -or
    (Get-RequiredPropertyValue -Object $application -Name 'applicationId') -cne (Get-WidgetApplicationId) -or
    (Get-RequiredPropertyValue -Object $application -Name 'executable') -ne 'CodexQuotaWidget.exe' -or
    (Get-RequiredPropertyValue -Object $application -Name 'runtimeIdentifier') -ne 'win-x64' -or
    -not [bool] (Get-RequiredPropertyValue -Object $application -Name 'selfContained')) {
    throw 'The application manifest does not describe a self-contained win-x64 build.'
}

if ((Get-RequiredPropertyValue -Object $codex -Name 'packagedPath') -ne 'runtime/codex.exe') {
    throw 'The manifest Codex path must be runtime/codex.exe.'
}

if ((Get-RequiredPropertyValue -Object $codex -Name 'packageName') -cne 'OpenAI.Codex' -or
    (Get-RequiredPropertyValue -Object $codex -Name 'packageFamilyName') -cne $expectedPackageFamilyName -or
    (Get-RequiredPropertyValue -Object $codex -Name 'publisherId') -cne $expectedPublisherId -or
    (Get-RequiredPropertyValue -Object $codex -Name 'signatureKind') -cne $expectedSignatureKind -or
    (Get-RequiredPropertyValue -Object $codex -Name 'packageStatus') -ne 'Ok' -or
    (Get-RequiredPropertyValue -Object $codex -Name 'packageArchitecture') -ne 'X64' -or
    (Get-RequiredPropertyValue -Object $codex -Name 'sourceRelativePath') -ne 'app/resources/codex.exe' -or
    [string]::IsNullOrWhiteSpace([string] (Get-RequiredPropertyValue -Object $codex -Name 'packageFullName')) -or
    [string]::IsNullOrWhiteSpace([string] (Get-RequiredPropertyValue -Object $codex -Name 'packageVersion'))) {
    throw 'The manifest does not identify the expected OpenAI.Codex Store source.'
}

$packageFullName = [string] (Get-RequiredPropertyValue -Object $codex -Name 'packageFullName')
$sourcePackages = @(
    Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
        Where-Object {
            [string] $_.PackageFullName -eq $packageFullName -and
            [string] $_.PackageFamilyName -ceq $expectedPackageFamilyName -and
            [string] $_.PublisherId -ceq $expectedPublisherId -and
            [string] $_.SignatureKind -ceq $expectedSignatureKind -and
            [string] $_.Status -eq 'Ok' -and
            [string] $_.Architecture -eq 'X64'
        }
)
if ($sourcePackages.Count -ne 1) {
    throw "The exact Store package '$packageFullName' used to build this artifact is no longer installed. Rebuild the artifact against the current Codex Store package."
}

$sourceCodexPath = Join-Path $sourcePackages[0].InstallLocation 'app\resources\codex.exe'
[void] (Assert-NoReparsePointInPath -Path $sourceCodexPath)
if (-not (Test-Path -LiteralPath $sourceCodexPath -PathType Leaf)) {
    throw "The installed Store package is missing its Codex runtime: '$sourceCodexPath'."
}

$packageVersionText = [string] (Get-RequiredPropertyValue -Object $codex -Name 'packageVersion')
try {
    [void] [version] $packageVersionText
}
catch {
    throw "The manifest packageVersion is malformed: '$packageVersionText'."
}
if ($packageVersionText -ne [string] $sourcePackages[0].Version) {
    throw "Manifest packageVersion '$packageVersionText' does not match installed Store package version '$($sourcePackages[0].Version)'. Rebuild the artifact."
}

$expectedHash = ([string] (Get-RequiredPropertyValue -Object $codex -Name 'sha256')).ToUpperInvariant()
if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
    throw 'The manifest Codex SHA-256 value is malformed.'
}
$actualHash = (Get-FileHash -LiteralPath $codexPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $expectedHash) {
    throw "Codex runtime hash mismatch. Manifest: $expectedHash; actual: $actualHash."
}
$sourceHash = (Get-FileHash -LiteralPath $sourceCodexPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($sourceHash -ne $actualHash) {
    throw "Artifact Codex runtime no longer matches Store package '$packageFullName'. Rebuild the artifact."
}

$expectedSize = [long] (Get-RequiredPropertyValue -Object $codex -Name 'fileSizeBytes')
$actualSize = (Get-Item -LiteralPath $codexPath).Length
if ($actualSize -ne $expectedSize) {
    throw "Codex runtime size mismatch. Manifest: $expectedSize; actual: $actualSize."
}

$signature = Get-AuthenticodeSignature -LiteralPath $codexPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate) {
    throw "Codex runtime Authenticode signature is not valid (status: $($signature.Status))."
}

$sourceSignature = Get-AuthenticodeSignature -LiteralPath $sourceCodexPath
if ($sourceSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $sourceSignature.SignerCertificate) {
    throw "The Store source Codex runtime Authenticode signature is not valid (status: $($sourceSignature.Status))."
}

$expectedSignatureStatus = [string] (Get-RequiredPropertyValue -Object $authenticode -Name 'status')
$expectedSignerSubject = [string] (Get-RequiredPropertyValue -Object $authenticode -Name 'signerSubject')
$expectedSignerThumbprint = [string] (Get-RequiredPropertyValue -Object $authenticode -Name 'signerThumbprint')
if ($expectedSignatureStatus -ne 'Valid' -or
    $signature.SignerCertificate.Subject -ne $expectedSignerSubject -or
    -not $signature.SignerCertificate.Thumbprint.Equals(
        $expectedSignerThumbprint,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Codex runtime Authenticode signer does not match runtime-manifest.json.'
}
if ($sourceSignature.SignerCertificate.Subject -ne $signature.SignerCertificate.Subject -or
    -not $sourceSignature.SignerCertificate.Thumbprint.Equals(
        $signature.SignerCertificate.Thumbprint,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact Codex runtime signer does not match Store package '$packageFullName'. Rebuild the artifact."
}

$x64Machine = 0x8664
if ((Get-PeMachine -Path $appPath) -ne $x64Machine) {
    throw 'CodexQuotaWidget.exe is not an x64 PE executable.'
}
if ((Get-PeMachine -Path $codexPath) -ne $x64Machine) {
    throw 'runtime\codex.exe is not an x64 PE executable.'
}

$cliVersionOutput = @(& $codexPath --version 2>&1)
if ($LASTEXITCODE -ne 0 -or $cliVersionOutput.Count -eq 0) {
    throw "runtime\codex.exe failed '--version' verification."
}
$actualCliVersion = (($cliVersionOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine).Trim()
$expectedCliVersion = [string] (Get-RequiredPropertyValue -Object $codex -Name 'cliVersion')
if ($actualCliVersion -ne $expectedCliVersion) {
    throw "Codex CLI version mismatch. Manifest: '$expectedCliVersion'; actual: '$actualCliVersion'."
}

$appServerHelpOutput = @(& $codexPath app-server --help 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "runtime\codex.exe failed 'app-server --help' verification."
}
$appServerHelp = ($appServerHelpOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
if (-not $appServerHelp.Contains('--stdio')) {
    throw "runtime\codex.exe does not advertise the required 'app-server --stdio' transport."
}

if ($LaunchSmokeTest) {
    Write-Host 'Running the application smoke-test mode...'
    $process = Start-Process `
        -FilePath $appPath `
        -ArgumentList '--smoke-test' `
        -WorkingDirectory $ArtifactPath `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit(15000)) {
        try {
            $process.Kill()
        }
        catch {
            # Preserve the primary timeout error below.
        }
        throw 'Application smoke test did not exit within 15 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Application smoke test failed with exit code $($process.ExitCode)."
    }
}

Write-Host "Artifact verification passed: '$ArtifactPath'."
Write-Host "Codex CLI: $actualCliVersion"
Write-Host "Codex SHA256: $actualHash"
Write-Host "Signer: $($signature.SignerCertificate.Subject)"
