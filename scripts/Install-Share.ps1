[CmdletBinding()]
param(
    [switch] $PrepareOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

function Assert-ShareRelativePath {
    param([Parameter(Mandatory)] [string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\') -or
        $Path.Contains(':') -or
        $Path.StartsWith('/') -or
        $Path.EndsWith('/') -or
        @($Path.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        throw "Unsafe share-package path: '$Path'."
    }
}

function Assert-ShareDirectory {
    param([Parameter(Mandatory)] [string] $Directory)

    $root = Get-NormalizedFullPath -Path $Directory
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $root)
    $manifestPath = Join-Path $root 'share-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The share package is missing share-manifest.json.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.packageKind -cne 'CodexQuotaWidget-share-win-x64' -or
        $manifest.applicationId -cne (Get-WidgetApplicationId) -or
        $null -eq $manifest.files -or
        @($manifest.files).Count -eq 0 -or
        @($manifest.files).Count -gt 5000) {
        throw 'The share package manifest is invalid.'
    }

    $expected = @{}
    $allowedDirectories = @{}
    foreach ($entry in @($manifest.files)) {
        $relativePath = [string] $entry.path
        Assert-ShareRelativePath -Path $relativePath
        if ($relativePath -ieq 'share-manifest.json' -or
            $relativePath -match '(^|/)(runtime|\.codex)(/|$)' -or
            $relativePath -match '(^|/)(settings\.json|widget\.log|runtime-manifest\.json)$' -or
            $relativePath -match '\.pdb$') {
            throw "Unexpected file in the share package: '$relativePath'."
        }
        if ($expected.ContainsKey($relativePath)) {
            throw "Duplicate share-package path: '$relativePath'."
        }
        $size = [long] $entry.sizeBytes
        $hash = [string] $entry.sha256
        if ($size -lt 0 -or $hash -cnotmatch '^[0-9A-F]{64}$') {
            throw "Invalid file record in share manifest: '$relativePath'."
        }
        $expected[$relativePath] = $entry

        $segments = $relativePath.Split('/')
        if ($segments.Length -gt 1) {
            for ($index = 1; $index -lt $segments.Length; $index++) {
                $allowedDirectories[($segments[0..($index - 1)] -join '/')] = $true
            }
        }
    }

    $guideLeaf = ([string] [char] 0x4F7F) + ([string] [char] 0x7528) +
        ([string] [char] 0x8BF4) + ([string] [char] 0x660E) + '.md'
    foreach ($required in @('CodexQuotaWidget.exe', 'CodexQuotaWidget.dll',
            'CodexQuotaWidget.Core.dll', 'CodexQuotaWidget.runtimeconfig.json',
            'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
            'Install.cmd', 'Install-Share.ps1', 'Install.ps1', 'Verify.ps1',
            'Uninstall.ps1', 'Common.ps1', $guideLeaf,
            (Get-WidgetOwnershipMarkerFileName))) {
        if (-not $expected.ContainsKey($required)) {
            throw "Required share-package file is missing: '$required'."
        }
    }
    [void] (Assert-WidgetOwnedDirectory -Directory $root)

    $actualFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
    if ($actualFiles.Count -ne $expected.Count + 1) {
        throw 'The share package contains missing or extra files.'
    }
    foreach ($file in $actualFiles) {
        $relativePath = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relativePath -ieq 'share-manifest.json') { continue }
        if (-not $expected.ContainsKey($relativePath)) {
            throw "The share package contains an extra file: '$relativePath'."
        }
        $entry = $expected[$relativePath]
        if ($file.Length -ne [long] $entry.sizeBytes -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant() -cne
                [string] $entry.sha256) {
            throw "Share-package file differs from its manifest: '$relativePath'."
        }
    }

    foreach ($childDirectory in @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Force)) {
        $relativePath = $childDirectory.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $allowedDirectories.ContainsKey($relativePath)) {
            throw "The share package contains an unexpected directory: '$relativePath'."
        }
    }
    return $manifest
}

$packageRoot = Get-NormalizedFullPath -Path $PSScriptRoot
[void] (Assert-ShareDirectory -Directory $packageRoot)

$expectedPackageFamilyName = 'OpenAI.Codex_2p2nqsd0c76g0'
$expectedPublisherId = '2p2nqsd0c76g0'
$packages = @(
    Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
        Where-Object {
            [string] $_.Name -ceq 'OpenAI.Codex' -and
            [string] $_.Architecture -eq 'X64' -and
            [string] $_.PackageFamilyName -ceq $expectedPackageFamilyName -and
            [string] $_.PublisherId -ceq $expectedPublisherId -and
            [string] $_.SignatureKind -ceq 'Store' -and
            [string] $_.Status -eq 'Ok'
        } |
        Sort-Object -Property @{ Expression = { [version] $_.Version } } -Descending
)
if ($packages.Count -eq 0) {
    throw 'Install and sign in to the official x64 Microsoft Store Codex app for this Windows user first.'
}
$package = $packages[0]
$sourceCodexPath = Join-Path $package.InstallLocation 'app\resources\codex.exe'
[void] (Assert-NoReparsePointInPath -Path $sourceCodexPath)
if (-not (Test-Path -LiteralPath $sourceCodexPath -PathType Leaf)) {
    throw 'The current user Codex Store package does not contain app/resources/codex.exe.'
}
$sourceSignature = Get-AuthenticodeSignature -LiteralPath $sourceCodexPath
if ($sourceSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $sourceSignature.SignerCertificate) {
    throw 'The official Codex runtime does not have a valid Authenticode signature.'
}
$sourceHash = (Get-FileHash -LiteralPath $sourceCodexPath -Algorithm SHA256).Hash.ToUpperInvariant()

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA must be available for a current-user installation.'
}
$tempParent = Get-NormalizedFullPath -Path (Join-Path $env:LOCALAPPDATA 'Temp')
[void] (Assert-NoReparsePointInPath -Path $tempParent)
if (-not (Test-Path -LiteralPath $tempParent -PathType Container)) {
    New-Item -ItemType Directory -Path $tempParent -Force | Out-Null
}
$stagingLeaf = 'CodexQuotaWidget.share-prep-' + [Guid]::NewGuid().ToString('N')
$stagingPath = Assert-SafeChildPath -Path (Join-Path $tempParent $stagingLeaf) `
    -AllowedParent $tempParent -ExpectedLeafName $stagingLeaf
if (Test-Path -LiteralPath $stagingPath) {
    throw "Refusing to reuse unexpected temporary directory '$stagingPath'."
}
New-Item -ItemType Directory -Path $stagingPath | Out-Null
[void] (Write-WidgetOwnershipMarker -Directory $stagingPath)

try {
    # The extracted share files have already been checked against the full file list.
    # This temporary artifact is the only place where the recipient's Codex CLI is copied.
    # Keep the verified package marker byte-for-byte: PowerShell versions may
    # serialize the same JSON differently, and the manifest records its hash.
    $markerName = Get-WidgetOwnershipMarkerFileName
    Copy-Item -LiteralPath (Join-Path $packageRoot $markerName) `
        -Destination (Join-Path $stagingPath $markerName) -Force
    foreach ($item in Get-ChildItem -LiteralPath $packageRoot -Force) {
        if ($item.Name -eq $markerName) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $stagingPath -Recurse
    }
    [void] (Assert-ShareDirectory -Directory $stagingPath)

    # The share bootstrap belongs only to the extracted package. The installed
    # program keeps the ordinary verification and uninstall scripts instead.
    $guideLeaf = ([string] [char] 0x4F7F) + ([string] [char] 0x7528) +
        ([string] [char] 0x8BF4) + ([string] [char] 0x660E) + '.md'
    foreach ($shareOnlyFile in @('Install.cmd', 'Install-Share.ps1',
            'share-manifest.json', $guideLeaf)) {
        Remove-Item -LiteralPath (Join-Path $stagingPath $shareOnlyFile) -Force
    }

    $runtimePath = Join-Path $stagingPath 'runtime'
    New-Item -ItemType Directory -Path $runtimePath | Out-Null
    $targetCodexPath = Join-Path $runtimePath 'codex.exe'
    Copy-Item -LiteralPath $sourceCodexPath -Destination $targetCodexPath
    $targetHash = (Get-FileHash -LiteralPath $targetCodexPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($targetHash -cne $sourceHash) {
        throw 'Copied Codex runtime SHA-256 differs from its official Store source.'
    }
    $targetSignature = Get-AuthenticodeSignature -LiteralPath $targetCodexPath
    if ($targetSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $targetSignature.SignerCertificate -or
        $sourceSignature.SignerCertificate.Subject -cne $targetSignature.SignerCertificate.Subject -or
        $sourceSignature.SignerCertificate.Thumbprint -cne $targetSignature.SignerCertificate.Thumbprint) {
        throw 'Copied Codex runtime signature differs from its official Store source.'
    }

    $cliVersionOutput = @(& $targetCodexPath --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $cliVersionOutput.Count -eq 0) {
        throw 'The official Codex runtime failed --version.'
    }
    $cliVersion = (($cliVersionOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($cliVersion)) {
        throw 'The official Codex runtime returned an empty version.'
    }
    $appPath = Join-Path $stagingPath 'CodexQuotaWidget.exe'
    $appVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appPath)
    $appVersion = [string] $appVersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($appVersion)) {
        $appVersion = [string] $appVersionInfo.FileVersion
    }
    $codexFile = Get-Item -LiteralPath $targetCodexPath
    $runtimeManifest = [ordered]@{
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
    Write-Utf8File -Path (Join-Path $stagingPath 'runtime-manifest.json') `
        -Content ($runtimeManifest | ConvertTo-Json -Depth 8)
    & (Join-Path $stagingPath 'Verify.ps1') -ArtifactPath $stagingPath

    if ($PrepareOnly) {
        Write-Host 'Preparation passed. The installed application and user settings were not changed.'
    }
    else {
        & (Join-Path $stagingPath 'Install.ps1') -ArtifactPath $stagingPath `
            -NoStartupShortcutOnFirstInstall
        $installedExecutable = Join-Path $env:LOCALAPPDATA 'Programs\CodexQuotaWidget\CodexQuotaWidget.exe'
        if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
            throw 'Installation completed without the expected widget executable.'
        }
        Start-Process -FilePath $installedExecutable -ArgumentList '--taskbar' `
            -WorkingDirectory (Split-Path -Parent $installedExecutable) -WindowStyle Hidden
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $stagingPath)
        Remove-SafeDirectory -Path $stagingPath -AllowedParent $tempParent -ExpectedLeafName $stagingLeaf
    }
}
