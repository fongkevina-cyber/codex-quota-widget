[CmdletBinding()]
param(
    [switch] $PurgeData
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
        throw "Shortcut has an invalid target: '$ShortcutPath'."
    }
    if (-not [string]::IsNullOrWhiteSpace($arguments)) {
        throw "Shortcut has unexpected arguments: '$ShortcutPath'."
    }

    $normalizedTargetPath = Get-NormalizedFullPath -Path $targetPath
    [void] (Assert-NoReparsePointInPath -Path $normalizedTargetPath)
    return $normalizedTargetPath
}

function Remove-ShortcutOnlyWhenOwned {
    param(
        [Parameter(Mandatory)]
        [string] $ShortcutPath,

        [Parameter(Mandatory)]
        [string] $ExpectedTargetPath
    )

    [void] (Assert-NoReparsePointInPath -Path $ShortcutPath)
    if (-not (Test-Path -LiteralPath $ShortcutPath)) {
        return
    }

    try {
        $actualTargetPath = Get-ShortcutTargetPath -ShortcutPath $ShortcutPath
        $expectedPath = Get-NormalizedFullPath -Path $ExpectedTargetPath
        if (-not $actualTargetPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Preserving unknown same-name shortcut '$ShortcutPath'. Its target is '$actualTargetPath', not the verified widget executable '$expectedPath'."
            return
        }

        Remove-Item -LiteralPath $ShortcutPath -Force
    }
    catch {
        Write-Warning "Preserving unknown same-name shortcut '$ShortcutPath'. $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or
    [string]::IsNullOrWhiteSpace($env:APPDATA)) {
    throw 'LOCALAPPDATA and APPDATA must be available for a current-user uninstall.'
}

$ownedInstallFiles = @(
    'CodexQuotaWidget.exe',
    'runtime\codex.exe',
    'runtime-manifest.json',
    'Uninstall.ps1',
    'Common.ps1'
)
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

$startupShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Codex Quota Widget.lnk'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Quota Widget.lnk'
$installedExe = Join-Path $installPath 'CodexQuotaWidget.exe'

if ($PurgeData -and (Test-Path -LiteralPath $dataPath)) {
    [void] (Assert-WidgetOwnedDirectory -Directory $dataPath)
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $dataPath)
}

if (Test-Path -LiteralPath $installPath) {
    [void] (Assert-WidgetOwnedDirectory `
        -Directory $installPath `
        -RequiredRelativePaths $ownedInstallFiles)

    $runningProcesses = @(Get-ProcessesUnderDirectory -Directory $installPath)
    if ($runningProcesses.Count -gt 0) {
        $processSummary = ($runningProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        throw "Exit the widget from its tray menu before uninstalling. Running: $processSummary."
    }

    Remove-ShortcutOnlyWhenOwned `
        -ShortcutPath $startupShortcut `
        -ExpectedTargetPath $installedExe
    Remove-ShortcutOnlyWhenOwned `
        -ShortcutPath $startMenuShortcut `
        -ExpectedTargetPath $installedExe

    [void] (Assert-WidgetOwnedDirectory `
        -Directory $installPath `
        -RequiredRelativePaths $ownedInstallFiles)
    Set-Location -LiteralPath $dataParent
    Remove-SafeDirectory `
        -Path $installPath `
        -AllowedParent $programsRoot `
        -ExpectedLeafName 'CodexQuotaWidget'
    Write-Host "Removed verified installation directory '$installPath'."
}
else {
    Write-Host "Installation directory does not exist: '$installPath'."
    foreach ($shortcutPath in @($startupShortcut, $startMenuShortcut)) {
        if (Test-Path -LiteralPath $shortcutPath) {
            Write-Warning "Preserving same-name shortcut because no verified installation exists: '$shortcutPath'."
        }
    }
}

if ($PurgeData) {
    if (Test-Path -LiteralPath $dataPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $dataPath)
        Remove-SafeDirectory `
            -Path $dataPath `
            -AllowedParent $dataParent `
            -ExpectedLeafName 'CodexQuotaWidget'
        Write-Host "Removed verified settings and logs directory '$dataPath'."
    }
}
else {
    Write-Host "Settings and logs were preserved at '$dataPath'. Use -PurgeData to remove them."
}
