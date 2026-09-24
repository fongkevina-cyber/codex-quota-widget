Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

function Get-NormalizedFullPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Equals($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath
    }

    $trimCharacters = [char[]] @(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    return $fullPath.TrimEnd($trimCharacters)
}

function Assert-NoReparsePointInPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $rootPath
    $separators = [char[]] @(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $segments = $fullPath.Substring($rootPath.Length).Split(
        $separators,
        [System.StringSplitOptions]::RemoveEmptyEntries)

    foreach ($segment in $segments) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing path whose existing ancestor is a reparse point: '$currentPath'."
        }
    }

    return $fullPath
}

function Assert-NoReparsePointInDirectoryTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Directory
    )

    $directoryPath = Get-NormalizedFullPath -Path $Directory
    [void] (Assert-NoReparsePointInPath -Path $directoryPath)
    if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        throw "Directory tree does not exist: '$directoryPath'."
    }

    $pendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
    $pendingDirectories.Push($directoryPath)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing directory tree containing a reparse point: '$($child.FullName)'."
            }
            if ($child.PSIsContainer) {
                $pendingDirectories.Push($child.FullName)
            }
        }
    }

    return $directoryPath
}

function Assert-SafeChildPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $AllowedParent,

        [string] $ExpectedLeafName
    )

    $fullPath = Get-NormalizedFullPath -Path $Path
    $fullParent = Get-NormalizedFullPath -Path $AllowedParent
    $prefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe path outside '$fullParent': '$fullPath'."
    }

    if ($ExpectedLeafName -and
        -not [System.IO.Path]::GetFileName($fullPath).Equals(
            $ExpectedLeafName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path with unexpected leaf name: '$fullPath'."
    }

    [void] (Assert-NoReparsePointInPath -Path $fullParent)
    [void] (Assert-NoReparsePointInPath -Path $fullPath)

    return $fullPath
}

function Get-WidgetOwnershipMarkerFileName {
    return '.codex-quota-widget-owner.json'
}

function Get-WidgetApplicationId {
    return '7d19329b-95b1-4e48-a71e-92370683a708'
}

function Assert-WidgetOwnedDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Directory,

        [string[]] $RequiredRelativePaths = @()
    )

    $directoryPath = Get-NormalizedFullPath -Path $Directory
    [void] (Assert-NoReparsePointInPath -Path $directoryPath)
    if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        throw "Owned directory does not exist: '$directoryPath'."
    }

    $markerPath = Join-Path $directoryPath (Get-WidgetOwnershipMarkerFileName)
    [void] (Assert-NoReparsePointInPath -Path $markerPath)
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Ownership marker is missing from '$directoryPath'."
    }

    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Ownership marker is invalid at '$markerPath': $($_.Exception.Message)"
    }

    $schemaProperty = $marker.PSObject.Properties['schemaVersion']
    $applicationIdProperty = $marker.PSObject.Properties['applicationId']
    $nameProperty = $marker.PSObject.Properties['name']
    if ($null -eq $schemaProperty -or
        ($schemaProperty.Value -isnot [int] -and
            $schemaProperty.Value -isnot [long]) -or
        [int] $schemaProperty.Value -ne 1 -or
        $null -eq $applicationIdProperty -or
        -not ([string] $applicationIdProperty.Value).Equals(
            (Get-WidgetApplicationId),
            [System.StringComparison]::Ordinal) -or
        $null -eq $nameProperty -or
        -not ([string] $nameProperty.Value).Equals(
            'CodexQuotaWidget',
            [System.StringComparison]::Ordinal)) {
        throw "Ownership marker does not belong to CodexQuotaWidget: '$markerPath'."
    }

    foreach ($relativePath in $RequiredRelativePaths) {
        if ([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Split($([char[]] @('\', '/'))) -contains '..') {
            throw "Required owned path must be a safe relative path: '$relativePath'."
        }

        $candidatePath = Get-NormalizedFullPath -Path (Join-Path $directoryPath $relativePath)
        $directoryPrefix = $directoryPath + [System.IO.Path]::DirectorySeparatorChar
        if (-not $candidatePath.StartsWith(
                $directoryPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Required owned path escapes '$directoryPath': '$relativePath'."
        }

        [void] (Assert-NoReparsePointInPath -Path $candidatePath)
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            throw "Owned directory is missing required file '$relativePath': '$directoryPath'."
        }
    }

    return $directoryPath
}

function Write-WidgetOwnershipMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Directory
    )

    $directoryPath = Get-NormalizedFullPath -Path $Directory
    [void] (Assert-NoReparsePointInPath -Path $directoryPath)
    if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        throw "Cannot mark a missing directory as owned: '$directoryPath'."
    }

    $markerPath = Join-Path $directoryPath (Get-WidgetOwnershipMarkerFileName)
    if (Test-Path -LiteralPath $markerPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $directoryPath)
        return $markerPath
    }

    $temporaryLeaf = (Get-WidgetOwnershipMarkerFileName) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $temporaryPath = Join-Path $directoryPath $temporaryLeaf
    $marker = [ordered]@{
        schemaVersion = 1
        applicationId = Get-WidgetApplicationId
        name = 'CodexQuotaWidget'
    }
    Write-Utf8File -Path $temporaryPath -Content ($marker | ConvertTo-Json -Depth 3)
    try {
        Move-Item -LiteralPath $temporaryPath -Destination $markerPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    [void] (Assert-WidgetOwnedDirectory -Directory $directoryPath)
    return $markerPath
}

function Initialize-WidgetOwnedDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Directory
    )

    $directoryPath = Get-NormalizedFullPath -Path $Directory
    [void] (Assert-NoReparsePointInPath -Path $directoryPath)
    if (Test-Path -LiteralPath $directoryPath) {
        if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
            throw "Expected an application directory but found a file: '$directoryPath'."
        }

        $markerPath = Join-Path $directoryPath (Get-WidgetOwnershipMarkerFileName)
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            [void] (Assert-WidgetOwnedDirectory -Directory $directoryPath)
            return $directoryPath
        }

        if (@(Get-ChildItem -LiteralPath $directoryPath -Force).Count -gt 0) {
            throw "Refusing to adopt non-empty unowned directory '$directoryPath'."
        }
    }
    else {
        New-Item -ItemType Directory -Path $directoryPath | Out-Null
    }

    [void] (Write-WidgetOwnershipMarker -Directory $directoryPath)
    return $directoryPath
}

function Remove-SafeDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $AllowedParent,

        [string] $ExpectedLeafName
    )

    $safePath = Assert-SafeChildPath `
        -Path $Path `
        -AllowedParent $AllowedParent `
        -ExpectedLeafName $ExpectedLeafName

    if (-not (Test-Path -LiteralPath $safePath)) {
        return
    }

    $item = Get-Item -LiteralPath $safePath -Force
    if (-not $item.PSIsContainer) {
        throw "Refusing recursive removal because the target is not a directory: '$safePath'."
    }

    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing recursive removal of a reparse point: '$safePath'."
    }

    [void] (Assert-NoReparsePointInDirectoryTree -Directory $safePath)

    Remove-Item -LiteralPath $safePath -Recurse -Force
}

function Get-ProcessesUnderDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Directory
    )

    $directoryPath = Get-NormalizedFullPath -Path $Directory
    $prefix = $directoryPath + [System.IO.Path]::DirectorySeparatorChar

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        try {
            $processPath = $process.Path
            if ($processPath -and
                (Get-NormalizedFullPath -Path $processPath).StartsWith(
                    $prefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $process
            }
        }
        catch {
            # Some protected system processes do not expose their executable path.
        }
    }
}

function Write-Utf8File {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}
