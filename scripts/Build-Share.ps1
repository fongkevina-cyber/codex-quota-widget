[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

. (Join-Path $PSScriptRoot 'Common.ps1')

$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..')
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$globalJsonPath = Join-Path $projectRoot 'global.json'
$appProjectPath = Join-Path $projectRoot 'src\CodexQuotaWidget.App\CodexQuotaWidget.App.csproj'
$guidePath = Join-Path $projectRoot 'docs\SHARE.md'
$installCmdPath = Join-Path $PSScriptRoot 'Install.cmd'
$shareRoot = Join-Path $projectRoot 'artifacts\share'
$stagingLeaf = '.building-' + [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $shareRoot $stagingLeaf
$zipLeaf = 'CodexQuotaWidget-share-win-x64-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') +
    '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip'
$zipPath = Join-Path $shareRoot $zipLeaf
$guideLeaf = ([string] [char] 0x4F7F) + ([string] [char] 0x7528) +
    ([string] [char] 0x8BF4) + ([string] [char] 0x660E) + '.md'

function Assert-NoLocalBuildPaths {
    param([Parameter(Mandatory)][string] $Directory)

    $userProfile = [Environment]::GetFolderPath('UserProfile').TrimEnd('\', '/') + '\'
    foreach ($assemblyName in @('CodexQuotaWidget.dll', 'CodexQuotaWidget.Core.dll')) {
        $assemblyPath = Join-Path $Directory $assemblyName
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "Share assembly is missing: '$assemblyPath'."
        }
        $bytes = [System.IO.File]::ReadAllBytes($assemblyPath)
        foreach ($encoding in @([System.Text.Encoding]::UTF8, [System.Text.Encoding]::Unicode)) {
            $content = $encoding.GetString($bytes)
            if ($content.IndexOf($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $content.IndexOf($userProfile, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $content.IndexOf('RSDS', [System.StringComparison]::Ordinal) -ge 0) {
                throw "Share assembly contains local build-path or debug information: '$assemblyPath'."
            }
        }
    }
}

[void] (Assert-NoReparsePointInPath -Path $projectRoot)
[void] (Assert-NoReparsePointInPath -Path $shareRoot)
foreach ($requiredPath in @($dotnetPath, $globalJsonPath, $appProjectPath,
        $guidePath, $installCmdPath,
        (Join-Path $PSScriptRoot 'Install-Share.ps1'),
        (Join-Path $PSScriptRoot 'Install.ps1'),
        (Join-Path $PSScriptRoot 'Verify.ps1'),
        (Join-Path $PSScriptRoot 'Uninstall.ps1'),
        (Join-Path $PSScriptRoot 'Common.ps1'))) {
    [void] (Assert-NoReparsePointInPath -Path $requiredPath)
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required build input is missing: '$requiredPath'."
    }
}

$expectedSdkVersion = [string] (
    Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
).sdk.version
$actualSdkVersion = (& $dotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdkVersion -ne $expectedSdkVersion) {
    throw "Expected project-local .NET SDK $expectedSdkVersion; found '$actualSdkVersion'."
}

New-Item -ItemType Directory -Path $shareRoot -Force | Out-Null
[void] (Assert-SafeChildPath -Path $stagingPath -AllowedParent $shareRoot -ExpectedLeafName $stagingLeaf)
[void] (Assert-SafeChildPath -Path $zipPath -AllowedParent $shareRoot -ExpectedLeafName $zipLeaf)
if (Test-Path -LiteralPath $stagingPath) {
    throw "Refusing to reuse unexpected staging directory '$stagingPath'."
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Refusing to replace an existing package '$zipPath'."
}

New-Item -ItemType Directory -Path $stagingPath | Out-Null
[void] (Write-WidgetOwnershipMarker -Directory $stagingPath)
$zipCreated = $false
try {
    Write-Host 'Rebuilding the generic share application and its project references...'
    & $dotnetPath build $appProjectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        -t:Rebuild `
        -p:ShareRelease=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Share rebuild failed with exit code $LASTEXITCODE. No dependency restore was attempted."
    }

    Write-Host 'Publishing the self-contained, generic share build...'
    & $dotnetPath publish $appProjectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --no-build `
        --output $stagingPath `
        -p:ShareRelease=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Share publish failed with exit code $LASTEXITCODE. No dependency restore was attempted."
    }

    Assert-NoLocalBuildPaths -Directory $stagingPath

    [void] (Assert-NoReparsePointInDirectoryTree -Directory $stagingPath)
    foreach ($symbolFile in @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse -Force -Filter '*.pdb')) {
        Remove-Item -LiteralPath $symbolFile.FullName -Force
    }

    foreach ($scriptName in @('Common.ps1', 'Install.ps1', 'Verify.ps1',
            'Uninstall.ps1', 'Install-Share.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) `
            -Destination (Join-Path $stagingPath $scriptName)
    }
    Copy-Item -LiteralPath $installCmdPath -Destination (Join-Path $stagingPath 'Install.cmd')
    Copy-Item -LiteralPath $guidePath -Destination (Join-Path $stagingPath $guideLeaf)

    $requiredFiles = @('CodexQuotaWidget.exe', 'CodexQuotaWidget.dll',
        'CodexQuotaWidget.Core.dll', 'CodexQuotaWidget.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
        'Common.ps1', 'Install.ps1', 'Verify.ps1', 'Uninstall.ps1',
        'Install-Share.ps1', 'Install.cmd', $guideLeaf,
        (Get-WidgetOwnershipMarkerFileName))
    [void] (Assert-WidgetOwnedDirectory -Directory $stagingPath -RequiredRelativePaths $requiredFiles)
    [void] (Assert-NoReparsePointInDirectoryTree -Directory $stagingPath)

    $files = @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse -Force)
    $entries = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($stagingPath.Length + 1).Replace('\', '/')
        if ($relativePath -match '(^|/)(runtime|\.codex)(/|$)' -or
            $relativePath -match '(^|/)(settings\.json|widget\.log|runtime-manifest\.json)$' -or
            $relativePath -match '\.pdb$' -or
            $relativePath -match '^share-manifest\.json$') {
            throw "Refusing to package a private or unexpected file: '$relativePath'."
        }
        $entries.Add([ordered]@{
                path = $relativePath
                sizeBytes = [long] $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            })
    }

    $sortedEntries = @($entries | Sort-Object -Property { $_['path'] })
    $manifest = [ordered]@{
        schemaVersion = 1
        packageKind = 'CodexQuotaWidget-share-win-x64'
        applicationId = Get-WidgetApplicationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        files = $sortedEntries
    }
    Write-Utf8File -Path (Join-Path $stagingPath 'share-manifest.json') `
        -Content ($manifest | ConvertTo-Json -Depth 7)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingPath, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipCreated = $true
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Write-Host "Share ZIP: $zipPath"
    Write-Host "SHA256: $zipHash"
    Write-Host "Files: $($sortedEntries.Count + 1)"
}
catch {
    if ($zipCreated -and (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        # This file was created by this run at an exact, previously absent path.
        Remove-Item -LiteralPath $zipPath -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [void] (Assert-WidgetOwnedDirectory -Directory $stagingPath)
        Remove-SafeDirectory -Path $stagingPath -AllowedParent $shareRoot -ExpectedLeafName $stagingLeaf
    }
}
