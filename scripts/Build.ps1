[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..')
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$globalJsonPath = Join-Path $projectRoot 'global.json'
$solutionPath = Join-Path $projectRoot 'CodexQuotaWidget.slnx'
$appProjectPath = Join-Path $projectRoot 'src\CodexQuotaWidget.App\CodexQuotaWidget.App.csproj'
$publishParent = Join-Path $projectRoot 'artifacts\publish'
$finalPublishPath = Join-Path $publishParent 'win-x64'
$publishStagingLeaf = 'win-x64.building-' + [Guid]::NewGuid().ToString('N')
$publishBackupLeaf = 'win-x64.previous-' + [Guid]::NewGuid().ToString('N')
$publishPath = Join-Path $publishParent $publishStagingLeaf
$publishBackupPath = Join-Path $publishParent $publishBackupLeaf
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

[void] (Assert-NoReparsePointInPath -Path $projectRoot)
[void] (Assert-NoReparsePointInPath -Path $dotnetPath)
[void] (Assert-NoReparsePointInPath -Path $publishParent)

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "The project-local SDK is missing. Run 'scripts\Setup-DotNet.ps1' first."
}

$expectedSdkVersion = [string] (
    Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
).sdk.version
$actualSdkVersion = (& $dotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdkVersion -ne $expectedSdkVersion) {
    throw "Expected project-local .NET SDK $expectedSdkVersion, but '$dotnetPath' reports '$actualSdkVersion'."
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found at '$solutionPath'."
}

if (-not $SkipTests) {
    Write-Host 'Running automated tests...'
    & $dotnetPath test $solutionPath `
        --configuration $Configuration `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $publishParent -Force | Out-Null
if (Test-Path -LiteralPath $finalPublishPath) {
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $finalPublishPath `
        -RequiredRelativePaths $ownedArtifactFiles)
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $finalPublishPath)
}
if (Test-Path -LiteralPath $publishPath) {
    throw "Refusing to reuse unexpected build staging directory '$publishPath'."
}
[void] (Assert-SafeChildPath `
    -Path $publishPath `
    -AllowedParent $publishParent `
    -ExpectedLeafName $publishStagingLeaf)
New-Item -ItemType Directory -Path $publishPath | Out-Null
[void] (Write-WidgetOwnershipMarker -Directory $publishPath)
$movedPreviousArtifact = $false
$movedStagedArtifact = $false

try {

Write-Host "Publishing the self-contained win-x64 application to '$publishPath'..."
& $dotnetPath publish $appProjectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$appPath = Join-Path $publishPath 'CodexQuotaWidget.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "Published executable not found at '$appPath'."
}

$packages = @(
    Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
        Where-Object {
            [string] $_.Name -ceq 'OpenAI.Codex' -and
            [string] $_.Architecture -eq 'X64' -and
            [string] $_.PackageFamilyName -ceq $expectedPackageFamilyName -and
            [string] $_.PublisherId -ceq $expectedPublisherId -and
            [string] $_.SignatureKind -ceq $expectedSignatureKind -and
            [string] $_.Status -eq 'Ok'
        } |
        Sort-Object -Property @{ Expression = { [version] $_.Version } } -Descending
)
if ($packages.Count -eq 0) {
    throw "The official x64 Microsoft Store package '$expectedPackageFamilyName' is not installed for the current user."
}

$package = $packages[0]
$sourceRelativePath = 'app\resources\codex.exe'
$sourceCodexPath = Join-Path $package.InstallLocation $sourceRelativePath
[void] (Assert-NoReparsePointInPath -Path $sourceCodexPath)
if (-not (Test-Path -LiteralPath $sourceCodexPath -PathType Leaf)) {
    throw "The Codex runtime was not found at the expected read-only package path '$sourceCodexPath'."
}

$sourceSignature = Get-AuthenticodeSignature -LiteralPath $sourceCodexPath
if ($sourceSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $sourceSignature.SignerCertificate) {
    throw "The Store Codex runtime does not have a valid Authenticode signature (status: $($sourceSignature.Status))."
}

$runtimePath = Join-Path $publishPath 'runtime'
New-Item -ItemType Directory -Path $runtimePath -Force | Out-Null
$targetCodexPath = Join-Path $runtimePath 'codex.exe'

Write-Host "Copying the signed Codex runtime from Store package $($package.PackageFullName)..."
Copy-Item -LiteralPath $sourceCodexPath -Destination $targetCodexPath -Force

$sourceHash = (Get-FileHash -LiteralPath $sourceCodexPath -Algorithm SHA256).Hash.ToUpperInvariant()
$targetHash = (Get-FileHash -LiteralPath $targetCodexPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($sourceHash -ne $targetHash) {
    throw "Codex runtime hash mismatch. Source: $sourceHash; target: $targetHash."
}

$targetSignature = Get-AuthenticodeSignature -LiteralPath $targetCodexPath
if ($targetSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $targetSignature.SignerCertificate) {
    throw "The copied Codex runtime does not have a valid Authenticode signature (status: $($targetSignature.Status))."
}

if ($sourceSignature.SignerCertificate.Thumbprint -ne $targetSignature.SignerCertificate.Thumbprint -or
    $sourceSignature.SignerCertificate.Subject -ne $targetSignature.SignerCertificate.Subject) {
    throw 'The copied Codex runtime signer does not match the Store source signer.'
}

$cliVersionOutput = @(& $targetCodexPath --version 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "The copied Codex runtime failed '--version' with exit code $LASTEXITCODE."
}
$cliVersion = ($cliVersionOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
$cliVersion = $cliVersion.Trim()
if ([string]::IsNullOrWhiteSpace($cliVersion)) {
    throw "The copied Codex runtime returned an empty '--version' value."
}

$appServerHelpOutput = @(& $targetCodexPath app-server --help 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "The copied Codex runtime failed 'app-server --help' with exit code $LASTEXITCODE."
}
$appServerHelp = ($appServerHelpOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
if (-not $appServerHelp.Contains('--stdio')) {
    throw "The copied Codex runtime does not advertise the required 'app-server --stdio' transport."
}

$appVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appPath)
$appVersion = [string] $appVersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    $appVersion = [string] $appVersionInfo.FileVersion
}

$codexFile = Get-Item -LiteralPath $targetCodexPath
$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    application = [ordered]@{
        name = 'CodexQuotaWidget'
        applicationId = Get-WidgetApplicationId
        version = $appVersion
        executable = 'CodexQuotaWidget.exe'
        runtimeIdentifier = 'win-x64'
        selfContained = $true
    }
    codex = [ordered]@{
        packagedPath = 'runtime/codex.exe'
        packageName = [string] $package.Name
        packageFullName = [string] $package.PackageFullName
        packageFamilyName = [string] $package.PackageFamilyName
        publisherId = [string] $package.PublisherId
        signatureKind = [string] $package.SignatureKind
        packageStatus = [string] $package.Status
        packageVersion = [string] $package.Version
        packageArchitecture = [string] $package.Architecture
        sourceRelativePath = 'app/resources/codex.exe'
        cliVersion = $cliVersion
        fileSizeBytes = [long] $codexFile.Length
        sha256 = $targetHash
        authenticode = [ordered]@{
            status = [string] $targetSignature.Status
            signerSubject = [string] $targetSignature.SignerCertificate.Subject
            signerThumbprint = [string] $targetSignature.SignerCertificate.Thumbprint
        }
    }
}

$manifestPath = Join-Path $publishPath 'runtime-manifest.json'
Write-Utf8File -Path $manifestPath -Content ($manifest | ConvertTo-Json -Depth 8)

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') `
    -Destination (Join-Path $publishPath 'Uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1') `
    -Destination (Join-Path $publishPath 'Common.ps1') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') `
    -Destination (Join-Path $publishPath 'README.md') -Force
[void] (Write-WidgetOwnershipMarker -Directory $publishPath)

& (Join-Path $PSScriptRoot 'Verify.ps1') -ArtifactPath $publishPath

if (Test-Path -LiteralPath $finalPublishPath) {
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $finalPublishPath `
        -RequiredRelativePaths $ownedArtifactFiles)
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $finalPublishPath)
    [void] (Assert-SafeChildPath `
        -Path $finalPublishPath `
        -AllowedParent $publishParent `
        -ExpectedLeafName 'win-x64')
    [void] (Assert-SafeChildPath `
        -Path $publishBackupPath `
        -AllowedParent $publishParent `
        -ExpectedLeafName $publishBackupLeaf)
    Move-Item -LiteralPath $finalPublishPath -Destination $publishBackupPath
    $movedPreviousArtifact = $true
}

[void] (Assert-WidgetOwnedDirectory `
    -Directory $publishPath `
    -RequiredRelativePaths $ownedArtifactFiles)
[void] (Assert-NoReparsePointInDirectoryTree -Directory $publishPath)
[void] (Assert-SafeChildPath `
    -Path $publishPath `
    -AllowedParent $publishParent `
    -ExpectedLeafName $publishStagingLeaf)
[void] (Assert-SafeChildPath `
    -Path $finalPublishPath `
    -AllowedParent $publishParent `
    -ExpectedLeafName 'win-x64')
Move-Item -LiteralPath $publishPath -Destination $finalPublishPath
$movedStagedArtifact = $true

if ($movedPreviousArtifact -and (Test-Path -LiteralPath $publishBackupPath)) {
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $publishBackupPath `
        -RequiredRelativePaths $ownedArtifactFiles)
    Remove-SafeDirectory `
        -Path $publishBackupPath `
        -AllowedParent $publishParent `
        -ExpectedLeafName $publishBackupLeaf
    $movedPreviousArtifact = $false
}
}
catch {
    if ($movedStagedArtifact -and (Test-Path -LiteralPath $finalPublishPath)) {
        [void] (Assert-WidgetOwnedDirectory `
            -Directory $finalPublishPath `
            -RequiredRelativePaths $ownedArtifactFiles)
        Remove-SafeDirectory `
            -Path $finalPublishPath `
            -AllowedParent $publishParent `
            -ExpectedLeafName 'win-x64'
        $movedStagedArtifact = $false
    }

    if ($movedPreviousArtifact -and (Test-Path -LiteralPath $publishBackupPath)) {
        [void] (Assert-WidgetOwnedDirectory `
            -Directory $publishBackupPath `
            -RequiredRelativePaths $ownedArtifactFiles)
        [void] (Assert-NoReparsePointInDirectoryTree -Directory $publishBackupPath)
        [void] (Assert-SafeChildPath `
            -Path $publishBackupPath `
            -AllowedParent $publishParent `
            -ExpectedLeafName $publishBackupLeaf)
        [void] (Assert-SafeChildPath `
            -Path $finalPublishPath `
            -AllowedParent $publishParent `
            -ExpectedLeafName 'win-x64')
        Move-Item -LiteralPath $publishBackupPath -Destination $finalPublishPath
        $movedPreviousArtifact = $false
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $publishPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $publishPath)
        Remove-SafeDirectory `
            -Path $publishPath `
            -AllowedParent $publishParent `
            -ExpectedLeafName $publishStagingLeaf
    }
}

Write-Host "Build complete: '$finalPublishPath'."
