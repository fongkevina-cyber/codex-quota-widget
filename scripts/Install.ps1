[CmdletBinding()]
param(
    [string] $ArtifactPath,

    [switch] $NoStartupShortcut,

    [switch] $NoStartupShortcutOnFirstInstall,

    [switch] $Launch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

function Get-ShortcutTargetPath {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath
    )

    [void] (Assert-NoReparsePointInPath -Path $ShortcutPath)
    $item = Get-Item -LiteralPath $ShortcutPath -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Shortcut path is not a regular file: '$ShortcutPath'."
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $targetPath = [Environment]::ExpandEnvironmentVariables([string] $shortcut.TargetPath)
        $arguments = [string] $shortcut.Arguments
    }
    finally {
        if ($null -ne $shortcut) {
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }

    if ([string]::IsNullOrWhiteSpace($targetPath) -or
        -not [System.IO.Path]::IsPathRooted($targetPath)) {
        throw "Shortcut has an invalid target and will not be managed: '$ShortcutPath'."
    }
    if (-not [string]::IsNullOrWhiteSpace($arguments)) {
        throw "Shortcut has unexpected arguments and will not be managed: '$ShortcutPath'."
    }

    $normalizedTargetPath = Get-NormalizedFullPath -Path $targetPath
    [void] (Assert-NoReparsePointInPath -Path $normalizedTargetPath)
    return $normalizedTargetPath
}

function Assert-ManagedShortcutIfPresent {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath,

        [Parameter(Mandatory)]
        [string] $ExpectedTargetPath,

        [Parameter(Mandatory)]
        [bool] $InstallIsVerified
    )

    [void] (Assert-NoReparsePointInPath -Path $ShortcutPath)
    if (-not (Test-Path -LiteralPath $ShortcutPath)) {
        return $false
    }

    if (-not $InstallIsVerified) {
        throw "Refusing to overwrite unknown same-name shortcut '$ShortcutPath' because no verified installation owns its target."
    }

    $actualTargetPath = Get-ShortcutTargetPath -ShortcutPath $ShortcutPath
    $expectedPath = Get-NormalizedFullPath -Path $ExpectedTargetPath
    if (-not $actualTargetPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to overwrite unknown same-name shortcut '$ShortcutPath'. Target is '$actualTargetPath', expected '$expectedPath'."
    }

    return $true
}

function New-UserShortcut {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath,

        [Parameter(Mandatory)]
        [string] $TargetPath,

        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [bool] $InstallIsVerified
    )

    [void] (Assert-ManagedShortcutIfPresent `
        -ShortcutPath $ShortcutPath `
        -ExpectedTargetPath $TargetPath `
        -InstallIsVerified $InstallIsVerified)

    $shortcutParent = Split-Path -Parent $ShortcutPath
    [void] (Assert-NoReparsePointInPath -Path $shortcutParent)
    New-Item -ItemType Directory -Path $shortcutParent -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = Split-Path -Parent $TargetPath
        $shortcut.IconLocation = "$TargetPath,0"
        $shortcut.Description = $Description
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shortcut) {
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }

    [void] (Assert-ManagedShortcutIfPresent `
        -ShortcutPath $ShortcutPath `
        -ExpectedTargetPath $TargetPath `
        -InstallIsVerified $InstallIsVerified)
}

function Remove-ManagedShortcutIfPresent {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath,

        [Parameter(Mandatory)]
        [string] $ExpectedTargetPath,

        [Parameter(Mandatory)]
        [bool] $InstallIsVerified
    )

    $isPresent = Assert-ManagedShortcutIfPresent `
        -ShortcutPath $ShortcutPath `
        -ExpectedTargetPath $ExpectedTargetPath `
        -InstallIsVerified $InstallIsVerified
    if ($isPresent) {
        Remove-Item -LiteralPath $ShortcutPath -Force
    }
}

function Get-RegularFileSnapshot {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    [void] (Assert-NoReparsePointInPath -Path $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            Existed = $false
            Base64 = $null
        }
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Cannot snapshot a non-regular file: '$Path'."
    }

    return [pscustomobject]@{
        Existed = $true
        Base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($Path))
    }
}

function Restore-ManagedShortcutSnapshot {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath,

        [Parameter(Mandatory)]
        [string] $ExpectedTargetPath,

        [Parameter(Mandatory)]
        [object] $Snapshot
    )

    [void] (Assert-NoReparsePointInPath -Path $ShortcutPath)
    $expectedPath = Get-NormalizedFullPath -Path $ExpectedTargetPath
    if (Test-Path -LiteralPath $ShortcutPath) {
        $actualTargetPath = Get-ShortcutTargetPath -ShortcutPath $ShortcutPath
        if (-not $actualTargetPath.Equals(
                $expectedPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to alter unknown shortcut during rollback: '$ShortcutPath'."
        }
    }

    if (-not [bool] $Snapshot.Existed) {
        if (Test-Path -LiteralPath $ShortcutPath) {
            Remove-Item -LiteralPath $ShortcutPath -Force
        }
        return
    }

    $shortcutParent = Split-Path -Parent $ShortcutPath
    [void] (Assert-NoReparsePointInPath -Path $shortcutParent)
    New-Item -ItemType Directory -Path $shortcutParent -Force | Out-Null
    $temporaryLeaf = [System.IO.Path]::GetFileNameWithoutExtension($ShortcutPath) +
        '.restore-' + [Guid]::NewGuid().ToString('N') + '.lnk'
    $temporaryPath = Join-Path $shortcutParent $temporaryLeaf
    [void] (Assert-NoReparsePointInPath -Path $temporaryPath)
    [System.IO.File]::WriteAllBytes(
        $temporaryPath,
        [Convert]::FromBase64String([string] $Snapshot.Base64))
    try {
        $restoredTargetPath = Get-ShortcutTargetPath -ShortcutPath $temporaryPath
        if (-not $restoredTargetPath.Equals(
                $expectedPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut rollback snapshot has an unexpected target: '$ShortcutPath'."
        }

        Move-Item -LiteralPath $temporaryPath -Destination $ShortcutPath -Force
        $verifiedTargetPath = Get-ShortcutTargetPath -ShortcutPath $ShortcutPath
        if (-not $verifiedTargetPath.Equals(
                $expectedPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut rollback verification failed: '$ShortcutPath'."
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            [void] (Assert-NoReparsePointInPath -Path $temporaryPath)
            $temporaryItem = Get-Item -LiteralPath $temporaryPath -Force
            if ($temporaryItem.PSIsContainer -or
                ($temporaryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove unsafe shortcut rollback temporary file: '$temporaryPath'."
            }
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Read-AutoStartPreference {
    param(
        [Parameter(Mandatory)]
        [string] $DataDirectory
    )

    $settingsPath = Join-Path $DataDirectory 'settings.json'
    [void] (Assert-NoReparsePointInPath -Path $settingsPath)
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "Application settings path is not a regular file: '$settingsPath'."
    }

    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Application settings are invalid and will not be overwritten: '$settingsPath'. $($_.Exception.Message)"
    }

    $property = $settings.PSObject.Properties['autoStart']
    if ($null -eq $property) {
        return $null
    }
    if ($property.Value -isnot [bool]) {
        throw "Application setting 'autoStart' is not a boolean: '$settingsPath'."
    }

    return [bool] $property.Value
}

function Write-AutoStartPreference {
    param(
        [Parameter(Mandatory)]
        [string] $DataDirectory,

        [Parameter(Mandatory)]
        [bool] $Enabled
    )

    [void] (Assert-WidgetOwnedDirectory -Directory $DataDirectory)
    $settingsPath = Join-Path $DataDirectory 'settings.json'
    [void] (Assert-NoReparsePointInPath -Path $settingsPath)
    if (Test-Path -LiteralPath $settingsPath) {
        if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
            throw "Application settings path is not a regular file: '$settingsPath'."
        }

        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            throw "Application settings are invalid and will not be overwritten: '$settingsPath'. $($_.Exception.Message)"
        }
        $property = $settings.PSObject.Properties['autoStart']
        if ($null -eq $property) {
            $settings | Add-Member -NotePropertyName 'autoStart' -NotePropertyValue $Enabled
        }
        else {
            $property.Value = $Enabled
        }
    }
    else {
        $settings = [ordered]@{
            schemaVersion = 1
            topmost = $true
            autoStart = $Enabled
            monitorDeviceName = $null
            leftPixels = $null
            topPixels = $null
        }
    }

    $temporaryPath = $settingsPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    Write-Utf8File -Path $temporaryPath -Content ($settings | ConvertTo-Json -Depth 10)
    try {
        Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Restore-SettingsSnapshot {
    param(
        [Parameter(Mandatory)]
        [string] $DataDirectory,

        [Parameter(Mandatory)]
        [object] $Snapshot
    )

    [void] (Assert-WidgetOwnedDirectory -Directory $DataDirectory)
    $settingsPath = Join-Path $DataDirectory 'settings.json'
    [void] (Assert-NoReparsePointInPath -Path $settingsPath)
    if (Test-Path -LiteralPath $settingsPath) {
        $settingsItem = Get-Item -LiteralPath $settingsPath -Force
        if ($settingsItem.PSIsContainer -or
            ($settingsItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to alter unsafe settings path during rollback: '$settingsPath'."
        }
    }

    if (-not [bool] $Snapshot.Existed) {
        if (Test-Path -LiteralPath $settingsPath) {
            Remove-Item -LiteralPath $settingsPath -Force
        }
        return
    }

    $temporaryPath = $settingsPath + '.restore-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [void] (Assert-NoReparsePointInPath -Path $temporaryPath)
    [System.IO.File]::WriteAllBytes(
        $temporaryPath,
        [Convert]::FromBase64String([string] $Snapshot.Base64))
    try {
        Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force
        $restoredBytes = [System.IO.File]::ReadAllBytes($settingsPath)
        $restoredBase64 = [Convert]::ToBase64String($restoredBytes)
        if (-not $restoredBase64.Equals(
                [string] $Snapshot.Base64,
                [System.StringComparison]::Ordinal)) {
            throw "Settings rollback verification failed: '$settingsPath'."
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            [void] (Assert-NoReparsePointInPath -Path $temporaryPath)
            $temporaryItem = Get-Item -LiteralPath $temporaryPath -Force
            if ($temporaryItem.PSIsContainer -or
                ($temporaryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove unsafe settings rollback temporary file: '$temporaryPath'."
            }
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Restore-DataDirectoryState {
    param(
        [Parameter(Mandatory)]
        [string] $DataDirectory,

        [Parameter(Mandatory)]
        [string] $AllowedParent,

        [Parameter(Mandatory)]
        [bool] $DirectoryExistedBefore,

        [Parameter(Mandatory)]
        [bool] $DirectoryWasOwnedBefore,

        [Parameter(Mandatory)]
        [object] $SettingsSnapshot
    )

    if (-not (Test-Path -LiteralPath $DataDirectory)) {
        if ($DirectoryExistedBefore) {
            throw "Application data directory disappeared during rollback: '$DataDirectory'."
        }
        return
    }

    [void] (Assert-SafeChildPath `
        -Path $DataDirectory `
        -AllowedParent $AllowedParent `
        -ExpectedLeafName 'CodexQuotaWidget')
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $DataDirectory)
    $markerPath = Join-Path $DataDirectory (Get-WidgetOwnershipMarkerFileName)
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        if ($DirectoryWasOwnedBefore) {
            throw "Owned application data directory lost its marker during rollback: '$DataDirectory'."
        }

        $entriesWithoutMarker = @(Get-ChildItem -LiteralPath $DataDirectory -Force)
        if ($entriesWithoutMarker.Count -gt 0) {
            throw "Refusing to restore an unmarked data directory containing unexpected entries: '$DataDirectory'."
        }
        if ($DirectoryExistedBefore) {
            return
        }

        [void] (Write-WidgetOwnershipMarker -Directory $DataDirectory)
    }

    [void] (Assert-WidgetOwnedDirectory -Directory $DataDirectory)
    if (-not $DirectoryExistedBefore) {
        $allowedNames = @(
            (Get-WidgetOwnershipMarkerFileName),
            'settings.json'
        )
        $unexpectedEntries = @(
            Get-ChildItem -LiteralPath $DataDirectory -Force |
                Where-Object {
                    $allowedNames -notcontains $_.Name -or
                    $_.PSIsContainer
                }
        )
        if ($unexpectedEntries.Count -gt 0) {
            $unexpectedNames = ($unexpectedEntries | ForEach-Object { $_.Name }) -join ', '
            throw "Refusing to remove newly created data directory containing unexpected entries: $unexpectedNames."
        }

        Remove-SafeDirectory `
            -Path $DataDirectory `
            -AllowedParent $AllowedParent `
            -ExpectedLeafName 'CodexQuotaWidget'
        return
    }

    Restore-SettingsSnapshot `
        -DataDirectory $DataDirectory `
        -Snapshot $SettingsSnapshot
    if ($DirectoryWasOwnedBefore) {
        return
    }

    [void] (Assert-WidgetOwnedDirectory -Directory $DataDirectory)
    [void] (Assert-NoReparsePointInPath -Path $markerPath)
    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if ($markerItem.PSIsContainer -or
        ($markerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove unsafe ownership marker during rollback: '$markerPath'."
    }
    Remove-Item -LiteralPath $markerPath -Force
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or
    [string]::IsNullOrWhiteSpace($env:APPDATA)) {
    throw 'LOCALAPPDATA and APPDATA must be available for a current-user installation.'
}

$ownedInstallFiles = @(
    'CodexQuotaWidget.exe',
    'runtime\codex.exe',
    'runtime-manifest.json',
    'Uninstall.ps1',
    'Common.ps1'
)
$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) {
    $ArtifactPath = Join-Path $projectRoot 'artifacts\publish\win-x64'
}
$ArtifactPath = Get-NormalizedFullPath -Path $ArtifactPath

& (Join-Path $PSScriptRoot 'Verify.ps1') -ArtifactPath $ArtifactPath

$programsRoot = Get-NormalizedFullPath -Path (Join-Path $env:LOCALAPPDATA 'Programs')
$installPath = Assert-SafeChildPath `
    -Path (Join-Path $programsRoot 'CodexQuotaWidget') `
    -AllowedParent $programsRoot `
    -ExpectedLeafName 'CodexQuotaWidget'
$dataParent = Get-NormalizedFullPath -Path $env:LOCALAPPDATA
$dataPath = Assert-SafeChildPath `
    -Path (Join-Path $dataParent 'CodexQuotaWidget') `
    -AllowedParent $dataParent `
    -ExpectedLeafName 'CodexQuotaWidget'
$stagingLeaf = 'CodexQuotaWidget.installing-' + [Guid]::NewGuid().ToString('N')
$backupLeaf = 'CodexQuotaWidget.previous-' + [Guid]::NewGuid().ToString('N')
$stagingPath = Assert-SafeChildPath `
    -Path (Join-Path $programsRoot $stagingLeaf) `
    -AllowedParent $programsRoot `
    -ExpectedLeafName $stagingLeaf
$backupPath = Assert-SafeChildPath `
    -Path (Join-Path $programsRoot $backupLeaf) `
    -AllowedParent $programsRoot `
    -ExpectedLeafName $backupLeaf

$startupShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Codex Quota Widget.lnk'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Quota Widget.lnk'
$hadPreviousInstall = Test-Path -LiteralPath $installPath
$previousInstallIsVerified = $false
$movedPreviousInstall = $false
$installedNewVersion = $false

if ($hadPreviousInstall) {
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $installPath `
        -RequiredRelativePaths $ownedInstallFiles)
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $installPath)
    $previousInstallIsVerified = $true

    $runningProcesses = @(Get-ProcessesUnderDirectory -Directory $installPath)
    if ($runningProcesses.Count -gt 0) {
        $processSummary = ($runningProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        throw "Close the installed widget before updating it. Running: $processSummary."
    }
}

$installedExe = Join-Path $installPath 'CodexQuotaWidget.exe'
$startupWasEnabled = Assert-ManagedShortcutIfPresent `
    -ShortcutPath $startupShortcut `
    -ExpectedTargetPath $installedExe `
    -InstallIsVerified $previousInstallIsVerified
$startupShortcutSnapshot = Get-RegularFileSnapshot -Path $startupShortcut
[void] (Assert-ManagedShortcutIfPresent `
    -ShortcutPath $startMenuShortcut `
    -ExpectedTargetPath $installedExe `
    -InstallIsVerified $previousInstallIsVerified)
$startMenuShortcutSnapshot = Get-RegularFileSnapshot -Path $startMenuShortcut

$dataPathExistedBefore = Test-Path -LiteralPath $dataPath
$dataPathWasOwnedBefore = $false
$settingsSnapshot = [pscustomobject]@{
    Existed = $false
    Base64 = $null
}
if ($dataPathExistedBefore) {
    [void] (Assert-NoReparsePointInPath -Path $dataPath)
    $dataItem = Get-Item -LiteralPath $dataPath -Force
    if (-not $dataItem.PSIsContainer -or
        ($dataItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Application data path is not a regular directory: '$dataPath'."
    }
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $dataPath)

    $existingMarkerPath = Join-Path $dataPath (Get-WidgetOwnershipMarkerFileName)
    if (Test-Path -LiteralPath $existingMarkerPath -PathType Leaf) {
        [void] (Assert-WidgetOwnedDirectory -Directory $dataPath)
        $dataPathWasOwnedBefore = $true
        $settingsSnapshot = Get-RegularFileSnapshot `
            -Path (Join-Path $dataPath 'settings.json')
    }
    elseif (@(Get-ChildItem -LiteralPath $dataPath -Force).Count -gt 0) {
        throw "Refusing to adopt non-empty unowned directory '$dataPath'."
    }
}

$enableStartup = $false
$dataMutationStarted = $false
$shortcutMutationStarted = $false

try {
    $dataMutationStarted = $true
    [void] (Initialize-WidgetOwnedDirectory -Directory $dataPath)
    $savedAutoStart = Read-AutoStartPreference -DataDirectory $dataPath
    if ($NoStartupShortcut -or
        ($NoStartupShortcutOnFirstInstall -and -not $hadPreviousInstall)) {
        $enableStartup = $false
    }
    elseif ($hadPreviousInstall) {
        $enableStartup = $startupWasEnabled -and $savedAutoStart -ne $false
    }
    else {
        $enableStartup = $savedAutoStart -ne $false
    }
    Write-AutoStartPreference -DataDirectory $dataPath -Enabled $enableStartup

    New-Item -ItemType Directory -Path $programsRoot -Force | Out-Null
    if (Test-Path -LiteralPath $stagingPath) {
        throw "Refusing to reuse unexpected staging directory '$stagingPath'."
    }
    New-Item -ItemType Directory -Path $stagingPath | Out-Null
    [void] (Write-WidgetOwnershipMarker -Directory $stagingPath)

    [void] (Assert-NoReparsePointInDirectoryTree -Directory $ArtifactPath)
    foreach ($artifactItem in Get-ChildItem -LiteralPath $ArtifactPath -Force) {
        Copy-Item -LiteralPath $artifactItem.FullName -Destination $stagingPath -Recurse -Force
    }

    [void] (Assert-WidgetOwnedDirectory `
        -Directory $stagingPath `
        -RequiredRelativePaths $ownedInstallFiles)
    & (Join-Path $PSScriptRoot 'Verify.ps1') -ArtifactPath $stagingPath

    if ($hadPreviousInstall) {
        [void] (Assert-WidgetOwnedDirectory `
            -Directory $installPath `
            -RequiredRelativePaths $ownedInstallFiles)
        [void] (Assert-NoReparsePointInDirectoryTree -Directory $installPath)
        [void] (Assert-SafeChildPath -Path $installPath -AllowedParent $programsRoot -ExpectedLeafName 'CodexQuotaWidget')
        [void] (Assert-SafeChildPath -Path $backupPath -AllowedParent $programsRoot -ExpectedLeafName $backupLeaf)
        Move-Item -LiteralPath $installPath -Destination $backupPath
        $movedPreviousInstall = $true
    }

    [void] (Assert-SafeChildPath -Path $stagingPath -AllowedParent $programsRoot -ExpectedLeafName $stagingLeaf)
    [void] (Assert-SafeChildPath -Path $installPath -AllowedParent $programsRoot -ExpectedLeafName 'CodexQuotaWidget')
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $stagingPath)
    Move-Item -LiteralPath $stagingPath -Destination $installPath
    $installedNewVersion = $true
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $installPath `
        -RequiredRelativePaths $ownedInstallFiles)
    & (Join-Path $PSScriptRoot 'Verify.ps1') -ArtifactPath $installPath

    $shortcutMutationStarted = $true
    New-UserShortcut `
        -ShortcutPath $startMenuShortcut `
        -TargetPath $installedExe `
        -Description 'Codex quota desktop widget' `
        -InstallIsVerified $true

    if ($enableStartup) {
        New-UserShortcut `
            -ShortcutPath $startupShortcut `
            -TargetPath $installedExe `
            -Description 'Start Codex quota desktop widget at sign-in' `
            -InstallIsVerified $true
    }
    else {
        Remove-ManagedShortcutIfPresent `
            -ShortcutPath $startupShortcut `
            -ExpectedTargetPath $installedExe `
            -InstallIsVerified $true
    }

}
catch {
    $originalErrorRecord = $_
    $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'

    if ($shortcutMutationStarted) {
        try {
            Restore-ManagedShortcutSnapshot `
                -ShortcutPath $startupShortcut `
                -ExpectedTargetPath $installedExe `
                -Snapshot $startupShortcutSnapshot
        }
        catch {
            [void] $rollbackErrors.Add(
                "Startup shortcut rollback failed: $($_.Exception.Message)")
        }

        try {
            Restore-ManagedShortcutSnapshot `
                -ShortcutPath $startMenuShortcut `
                -ExpectedTargetPath $installedExe `
                -Snapshot $startMenuShortcutSnapshot
        }
        catch {
            [void] $rollbackErrors.Add(
                "Start menu shortcut rollback failed: $($_.Exception.Message)")
        }
    }

    if ($installedNewVersion) {
        try {
            if (Test-Path -LiteralPath $installPath) {
                [void] (Assert-WidgetOwnedDirectory `
                    -Directory $installPath `
                    -RequiredRelativePaths $ownedInstallFiles)
                Remove-SafeDirectory `
                    -Path $installPath `
                    -AllowedParent $programsRoot `
                    -ExpectedLeafName 'CodexQuotaWidget'
            }
            $installedNewVersion = $false
        }
        catch {
            [void] $rollbackErrors.Add(
                "New installation rollback failed: $($_.Exception.Message)")
        }
    }

    if ($movedPreviousInstall) {
        try {
            if (Test-Path -LiteralPath $installPath) {
                throw "Cannot restore the previous installation because the destination still exists: '$installPath'."
            }
            if (-not (Test-Path -LiteralPath $backupPath -PathType Container)) {
                throw "Previous installation backup is missing: '$backupPath'."
            }

            [void] (Assert-WidgetOwnedDirectory `
                -Directory $backupPath `
                -RequiredRelativePaths $ownedInstallFiles)
            [void] (Assert-NoReparsePointInDirectoryTree -Directory $backupPath)
            [void] (Assert-SafeChildPath -Path $backupPath -AllowedParent $programsRoot -ExpectedLeafName $backupLeaf)
            [void] (Assert-SafeChildPath -Path $installPath -AllowedParent $programsRoot -ExpectedLeafName 'CodexQuotaWidget')
            Move-Item -LiteralPath $backupPath -Destination $installPath
            $movedPreviousInstall = $false
        }
        catch {
            [void] $rollbackErrors.Add(
                "Previous installation rollback failed: $($_.Exception.Message)")
        }
    }

    if ($dataMutationStarted) {
        try {
            Restore-DataDirectoryState `
                -DataDirectory $dataPath `
                -AllowedParent $dataParent `
                -DirectoryExistedBefore $dataPathExistedBefore `
                -DirectoryWasOwnedBefore $dataPathWasOwnedBefore `
                -SettingsSnapshot $settingsSnapshot
        }
        catch {
            [void] $rollbackErrors.Add(
                "Application data rollback failed: $($_.Exception.Message)")
        }
    }

    if ($rollbackErrors.Count -gt 0) {
        $rollbackSummary = ($rollbackErrors | ForEach-Object { [string] $_ }) -join ' | '
        $message = "Installation failed: $($originalErrorRecord.Exception.Message) Rollback errors: $rollbackSummary"
        $rollbackException = New-Object System.InvalidOperationException `
            -ArgumentList @($message, $originalErrorRecord.Exception)
        throw $rollbackException
    }

    throw $originalErrorRecord
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $stagingPath)
        Remove-SafeDirectory `
            -Path $stagingPath `
            -AllowedParent $programsRoot `
            -ExpectedLeafName $stagingLeaf
    }
}

if ($movedPreviousInstall -and (Test-Path -LiteralPath $backupPath)) {
    try {
        [void] (Assert-WidgetOwnedDirectory `
            -Directory $backupPath `
            -RequiredRelativePaths $ownedInstallFiles)
        [void] (Assert-NoReparsePointInDirectoryTree -Directory $backupPath)
        Remove-SafeDirectory `
            -Path $backupPath `
            -AllowedParent $programsRoot `
            -ExpectedLeafName $backupLeaf
        $movedPreviousInstall = $false
    }
    catch {
        Write-Warning "The new installation is valid, but the previous-version backup could not be fully removed: '$backupPath'. $($_.Exception.Message)"
    }
}

Write-Host "Installed Codex Quota Widget at '$installPath'."
Write-Host "Start menu shortcut: '$startMenuShortcut'."
if ($enableStartup) {
    Write-Host "Startup shortcut enabled: '$startupShortcut'."
}
else {
    Write-Host 'Startup shortcut is disabled.'
}

if ($Launch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $installPath
}
