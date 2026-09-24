[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

function Get-UsableSdkVersion {
    param(
        [Parameter(Mandatory)]
        [string] $SdkRoot
    )

    $dotnetExecutable = Join-Path $SdkRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetExecutable -PathType Leaf)) {
        return $null
    }

    try {
        $versionOutput = @(& $dotnetExecutable --version 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        $reportedVersion = (($versionOutput | ForEach-Object { [string] $_ }) -join '').Trim()
        if ([string]::IsNullOrWhiteSpace($reportedVersion)) {
            return $null
        }

        foreach ($requiredSdkFile in @(
            "sdk\$reportedVersion\dotnet.dll",
            "sdk\$reportedVersion\MSBuild.dll",
            "sdk\$reportedVersion\Roslyn\bincore\csc.dll"
        )) {
            if (-not (Test-Path -LiteralPath (Join-Path $SdkRoot $requiredSdkFile) -PathType Leaf)) {
                return $null
            }
        }

        $infoOutput = @(& $dotnetExecutable --info 2>$null)
        if ($LASTEXITCODE -ne 0 -or $infoOutput.Count -eq 0) {
            return $null
        }

        return $reportedVersion
    }
    catch {
        return $null
    }
}

function Remove-SafeDownloadFile {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $DownloadRoot,

        [Parameter(Mandatory)]
        [string] $ExpectedLeafName
    )

    $safePath = Assert-SafeChildPath `
        -Path $Path `
        -AllowedParent $DownloadRoot `
        -ExpectedLeafName $ExpectedLeafName
    if (Test-Path -LiteralPath $safePath) {
        $item = Get-Item -LiteralPath $safePath -Force
        if ($item.PSIsContainer -or
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove a non-regular download file: '$safePath'."
        }

        Remove-Item -LiteralPath $safePath -Force
    }
}

function Assert-RegularDirectoryIfExists {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Expected a regular directory but found an unsafe path: '$Path'."
    }
}

$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..')
$toolsRoot = Get-NormalizedFullPath -Path (Join-Path $projectRoot '.tools')
$installRoot = Assert-SafeChildPath `
    -Path (Join-Path $toolsRoot 'dotnet') `
    -AllowedParent $toolsRoot `
    -ExpectedLeafName 'dotnet'
$globalJsonPath = Join-Path $projectRoot 'global.json'
$downloadRoot = Assert-SafeChildPath `
    -Path (Join-Path $toolsRoot 'downloads') `
    -AllowedParent $toolsRoot `
    -ExpectedLeafName 'downloads'

Assert-RegularDirectoryIfExists -Path $toolsRoot
Assert-RegularDirectoryIfExists -Path $installRoot
Assert-RegularDirectoryIfExists -Path $downloadRoot

if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "global.json was not found at '$globalJsonPath'."
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sdkVersion = [string] $globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw 'global.json does not contain sdk.version.'
}

$downloadLeaf = "dotnet-sdk-$sdkVersion-win-x64.zip"
$downloadPath = Assert-SafeChildPath `
    -Path (Join-Path $downloadRoot $downloadLeaf) `
    -AllowedParent $downloadRoot `
    -ExpectedLeafName $downloadLeaf

$installedVersion = Get-UsableSdkVersion -SdkRoot $installRoot
if ($installedVersion -eq $sdkVersion) {
    if (Test-Path -LiteralPath $downloadPath) {
        Remove-SafeDownloadFile `
            -Path $downloadPath `
            -DownloadRoot $downloadRoot `
            -ExpectedLeafName $downloadLeaf
    }

    Write-Host "Portable .NET SDK $installedVersion is already installed and usable."
    exit 0
}

if ($installedVersion -and -not $Force) {
    throw "A usable SDK $installedVersion exists at '$installRoot'. Re-run with -Force to replace it with $sdkVersion after staging validation."
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw 'curl.exe is required to download the official .NET SDK archive.'
}

New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null

$metadataUri = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
Write-Host "Reading official .NET 10 release metadata from '$metadataUri'..."
$metadataOutput = @(
    & curl.exe `
        --location `
        --fail `
        --silent `
        --show-error `
        --retry 5 `
        --retry-delay 2 `
        $metadataUri
)
if ($LASTEXITCODE -ne 0) {
    throw "curl.exe failed to read .NET release metadata with exit code $LASTEXITCODE."
}

try {
    $metadata = (($metadataOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine) |
        ConvertFrom-Json
}
catch {
    throw "Official .NET release metadata could not be parsed: $($_.Exception.Message)"
}

if ([string] $metadata.'channel-version' -ne '10.0') {
    throw "Unexpected .NET release metadata channel '$($metadata.'channel-version')'."
}

$releaseMatches = @($metadata.releases | Where-Object { [string] $_.sdk.version -eq $sdkVersion })
if ($releaseMatches.Count -ne 1) {
    throw "Expected one .NET SDK $sdkVersion entry in official metadata, found $($releaseMatches.Count)."
}

$sdkFiles = @(
    $releaseMatches[0].sdk.files |
        Where-Object {
            [string] $_.rid -eq 'win-x64' -and
            [string] $_.name -eq 'dotnet-sdk-win-x64.zip'
        }
)
if ($sdkFiles.Count -ne 1) {
    throw "Expected one win-x64 SDK zip for .NET SDK $sdkVersion, found $($sdkFiles.Count)."
}

$sdkFile = $sdkFiles[0]
$sdkUri = [string] $sdkFile.url
$expectedSha512 = ([string] $sdkFile.hash).ToUpperInvariant()
if (-not [Uri]::IsWellFormedUriString($sdkUri, [UriKind]::Absolute) -or
    -not $sdkUri.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Official metadata returned an invalid SDK URL: '$sdkUri'."
}
if ($expectedSha512 -notmatch '^[0-9A-F]{128}$') {
    throw 'Official metadata returned a malformed SDK SHA-512 value.'
}

$headOutput = @(
    & curl.exe `
        --head `
        --location `
        --fail `
        --silent `
        --show-error `
        --retry 5 `
        --retry-delay 2 `
        $sdkUri
)
if ($LASTEXITCODE -ne 0) {
    throw "curl.exe failed to read SDK archive headers with exit code $LASTEXITCODE."
}

$contentLengths = @(
    $headOutput |
        ForEach-Object {
            if ([string] $_ -match '^Content-Length:\s*(\d+)\s*$') {
                [long] $Matches[1]
            }
        }
)
if ($contentLengths.Count -eq 0 -or $contentLengths[-1] -le 0) {
    throw 'The official SDK download did not provide a valid Content-Length header.'
}
$expectedLength = [long] $contentLengths[-1]

$downloadIsValid = $false
if (Test-Path -LiteralPath $downloadPath -PathType Container) {
    throw "Refusing to use a directory as the SDK download file: '$downloadPath'."
}
if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
    $existingDownload = Get-Item -LiteralPath $downloadPath -Force
    if (($existingDownload.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a reparse point as the SDK download: '$downloadPath'."
    }

    $existingLength = $existingDownload.Length
    if ($existingLength -eq $expectedLength) {
        $existingHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA512).Hash.ToUpperInvariant()
        $downloadIsValid = $existingHash -eq $expectedSha512
        if (-not $downloadIsValid) {
            Remove-SafeDownloadFile `
                -Path $downloadPath `
                -DownloadRoot $downloadRoot `
                -ExpectedLeafName $downloadLeaf
        }
    }
    elseif ($existingLength -gt $expectedLength) {
        Remove-SafeDownloadFile `
            -Path $downloadPath `
            -DownloadRoot $downloadRoot `
            -ExpectedLeafName $downloadLeaf
    }
}

if (-not $downloadIsValid) {
    Write-Host "Downloading .NET SDK $sdkVersion for win-x64 with resume support..."
    & curl.exe `
        --location `
        --fail `
        --show-error `
        --retry 5 `
        --retry-delay 2 `
        --continue-at - `
        --output $downloadPath `
        $sdkUri
    if ($LASTEXITCODE -ne 0) {
        throw "curl.exe failed to download the SDK archive with exit code $LASTEXITCODE. The partial file is retained for a later resume."
    }
}

$actualLength = (Get-Item -LiteralPath $downloadPath).Length
if ($actualLength -ne $expectedLength) {
    throw "SDK archive length mismatch. Expected $expectedLength bytes; downloaded $actualLength bytes."
}

$actualSha512 = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA512).Hash.ToUpperInvariant()
if ($actualSha512 -ne $expectedSha512) {
    Remove-SafeDownloadFile `
        -Path $downloadPath `
        -DownloadRoot $downloadRoot `
        -ExpectedLeafName $downloadLeaf
    throw 'SDK archive SHA-512 mismatch. The invalid download was removed; re-run to download a clean copy.'
}

$stagingLeaf = 'dotnet.staging-' + [Guid]::NewGuid().ToString('N')
$backupLeaf = 'dotnet.previous-' + [Guid]::NewGuid().ToString('N')
$stagingRoot = Assert-SafeChildPath `
    -Path (Join-Path $toolsRoot $stagingLeaf) `
    -AllowedParent $toolsRoot `
    -ExpectedLeafName $stagingLeaf
$backupRoot = Assert-SafeChildPath `
    -Path (Join-Path $toolsRoot $backupLeaf) `
    -AllowedParent $toolsRoot `
    -ExpectedLeafName $backupLeaf

$movedPreviousInstall = $false
$movedStagedInstall = $false

New-Item -ItemType Directory -Path $stagingRoot | Out-Null
try {
    Write-Host "Extracting SDK to staging directory '$stagingRoot'..."
    Expand-Archive -LiteralPath $downloadPath -DestinationPath $stagingRoot -Force

    $stagedVersion = Get-UsableSdkVersion -SdkRoot $stagingRoot
    if ($stagedVersion -ne $sdkVersion) {
        throw "Staged SDK validation failed. Expected $sdkVersion; reported '$stagedVersion'."
    }

    if (Test-Path -LiteralPath $installRoot) {
        $existingItem = Get-Item -LiteralPath $installRoot -Force
        if (-not $existingItem.PSIsContainer -or
            ($existingItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to replace a non-regular SDK directory: '$installRoot'."
        }

        [void] (Assert-SafeChildPath -Path $installRoot -AllowedParent $toolsRoot -ExpectedLeafName 'dotnet')
        [void] (Assert-SafeChildPath -Path $backupRoot -AllowedParent $toolsRoot -ExpectedLeafName $backupLeaf)
        [void] (Assert-NoReparsePointInDirectoryTree -Directory $installRoot)
        Move-Item -LiteralPath $installRoot -Destination $backupRoot
        $movedPreviousInstall = $true
    }

    [void] (Assert-SafeChildPath -Path $stagingRoot -AllowedParent $toolsRoot -ExpectedLeafName $stagingLeaf)
    [void] (Assert-SafeChildPath -Path $installRoot -AllowedParent $toolsRoot -ExpectedLeafName 'dotnet')
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $stagingRoot)
    Move-Item -LiteralPath $stagingRoot -Destination $installRoot
    $movedStagedInstall = $true

    $finalVersion = Get-UsableSdkVersion -SdkRoot $installRoot
    if ($finalVersion -ne $sdkVersion) {
        throw "Final SDK validation failed. Expected $sdkVersion; reported '$finalVersion'."
    }

    if ($movedPreviousInstall -and (Test-Path -LiteralPath $backupRoot)) {
        Remove-SafeDirectory `
            -Path $backupRoot `
            -AllowedParent $toolsRoot `
            -ExpectedLeafName $backupLeaf
        $movedPreviousInstall = $false
    }
}
catch {
    if ($movedStagedInstall -and (Test-Path -LiteralPath $installRoot)) {
        Remove-SafeDirectory `
            -Path $installRoot `
            -AllowedParent $toolsRoot `
            -ExpectedLeafName 'dotnet'
        $movedStagedInstall = $false
    }

    if ($movedPreviousInstall -and (Test-Path -LiteralPath $backupRoot)) {
        [void] (Assert-SafeChildPath -Path $backupRoot -AllowedParent $toolsRoot -ExpectedLeafName $backupLeaf)
        [void] (Assert-SafeChildPath -Path $installRoot -AllowedParent $toolsRoot -ExpectedLeafName 'dotnet')
        [void] (Assert-NoReparsePointInDirectoryTree -Directory $backupRoot)
        Move-Item -LiteralPath $backupRoot -Destination $installRoot
        $movedPreviousInstall = $false
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-SafeDirectory `
            -Path $stagingRoot `
            -AllowedParent $toolsRoot `
            -ExpectedLeafName $stagingLeaf
    }
}

Remove-SafeDownloadFile `
    -Path $downloadPath `
    -DownloadRoot $downloadRoot `
    -ExpectedLeafName $downloadLeaf

Write-Host "Installed and verified portable .NET SDK $sdkVersion at '$installRoot'."
