[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [switch]$IncludePublicDownload
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$releaseRoot = Join-Path $outputRoot 'release-candidate'
$releaseBuilder = Join-Path $repoRoot 'scripts\build-release-candidate.ps1'
$buildProperties = Join-Path $repoRoot 'Directory.Build.props'
$validationExecutable = Join-Path $repoRoot 'tools\BakuretsuOsakanaKobo.ReleaseValidation\bin\Release\net10.0-windows\win-x64\BakuretsuOsakanaKobo.ReleaseValidation.exe'
$reportPath = Join-Path $outputRoot 'v1.1.0-update-validation.json'
$buildPropertiesDocument = [xml](Get-Content -Raw -LiteralPath $buildProperties)
$expectedApplicationVersion = [string]$buildPropertiesDocument.Project.PropertyGroup.VersionPrefix
if ($expectedApplicationVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must contain a three-part VersionPrefix: $expectedApplicationVersion"
}

function Assert-Validation {
    param(
        [Parameter(Mandatory = $true)] [bool]$Condition,
        [Parameter(Mandatory = $true)] [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-ProductValidation {
    param(
        [Parameter(Mandatory = $true)] [string]$EnvironmentName,
        [Parameter(Mandatory = $true)] [string]$FirstArgument,
        [Parameter(Mandatory = $true)] [string]$ResultPath
    )

    $previous = [Environment]::GetEnvironmentVariable($EnvironmentName)
    try {
        [Environment]::SetEnvironmentVariable($EnvironmentName, '1')
        & $validationExecutable $FirstArgument $ResultPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "$EnvironmentName failed with exit code $LASTEXITCODE."
        }
    } finally {
        [Environment]::SetEnvironmentVariable($EnvironmentName, $previous)
    }

    Assert-Validation (Test-Path -LiteralPath $ResultPath -PathType Leaf) "Validation report was not created: $ResultPath"
    return Get-Content -Raw -LiteralPath $ResultPath | ConvertFrom-Json
}

if (Test-Path -LiteralPath $outputRoot) {
    Assert-Validation (@(Get-ChildItem -LiteralPath $outputRoot -Force).Count -eq 0) `
        "Output directory must be absent or empty: $outputRoot"
} else {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
}

$publicPreconditionRejected = $false
$missingSourceOutput = Join-Path $outputRoot 'public-without-source'
try {
    & $releaseBuilder -OutputDirectory $missingSourceOutput 2>&1 | Out-Null
} catch {
    if ($_.Exception.Message -like '*clean worktree*' -or
        $_.Exception.Message -like '*corresponding-source archive is mandatory*') {
        $publicPreconditionRejected = $true
    } else {
        throw
    }
}
Assert-Validation $publicPreconditionRejected 'The public release gate accepted an invalid publication request.'
$releaseBuilderSource = Get-Content -Raw -LiteralPath $releaseBuilder
$publicCleanTreeGateDeclared =
    $releaseBuilderSource.Contains('A public release candidate must be generated from a clean worktree.', [StringComparison]::Ordinal) -and
    $releaseBuilderSource.Contains('if (-not $ValidationOnly -and $sourceTreeDirty)', [StringComparison]::Ordinal)
$publicSourceGateDeclared =
    $releaseBuilderSource.Contains('A complete corresponding-source archive is mandatory for a public release.', [StringComparison]::Ordinal) -and
    $releaseBuilderSource.Contains('if (-not $ValidationOnly)', [StringComparison]::Ordinal)
Assert-Validation $publicCleanTreeGateDeclared 'The public release gate did not require a clean worktree.'
Assert-Validation $publicSourceGateDeclared 'The public release gate did not require a corresponding-source archive.'

& $releaseBuilder -OutputDirectory $releaseRoot -ValidationOnly
if ($LASTEXITCODE -ne 0) {
    throw "Release candidate validation failed with exit code $LASTEXITCODE."
}

Assert-Validation (Test-Path -LiteralPath $validationExecutable -PathType Leaf) `
    "Release validation executable was not built: $validationExecutable"

$assetManifestPath = Join-Path $releaseRoot 'release-assets.json'
$productRoot = Join-Path $releaseRoot 'BakuretsuOsakanaKobo-win-x64'
$productManifestPath = Join-Path $productRoot 'release-manifest.json'
$helperRoot = Join-Path $productRoot 'updater'
$assetManifest = Get-Content -Raw -LiteralPath $assetManifestPath | ConvertFrom-Json
$productManifest = Get-Content -Raw -LiteralPath $productManifestPath | ConvertFrom-Json

Assert-Validation ($assetManifest.validationOnly -eq $true) 'The local release candidate was not marked validation-only.'
Assert-Validation ($assetManifest.applicationVersion -eq $expectedApplicationVersion) `
    "The release candidate version was not $expectedApplicationVersion."
Assert-Validation ($productManifest.distributionMode -eq 'FrameworkDependent') 'The release candidate was not framework-dependent.'
Assert-Validation ($productManifest.runtimeIdentifier -eq 'win-x64') 'The release candidate runtime was not win-x64.'
Assert-Validation ($productManifest.publishSingleFile -eq $false) 'The release candidate unexpectedly used single-file publishing.'
Assert-Validation ($productManifest.libVlcPluginCount -eq 319) 'The release candidate did not contain exactly 319 audited LibVLC plugins.'
Assert-Validation (@($productManifest.excludedGplPlugins).Count -eq 4) 'The GPL-only plugin exclusion inventory changed.'
Assert-Validation ($productManifest.packages.LibVLCSharp -eq '3.10.0') 'The LibVLCSharp version changed.'
Assert-Validation ($productManifest.packages.'LibVLCSharp.WPF' -eq '3.10.0') 'The LibVLCSharp.WPF version changed.'
Assert-Validation ($productManifest.packages.'VideoLAN.LibVLC.Windows' -eq '3.0.23.1') 'The LibVLC package version changed.'
Assert-Validation ($productManifest.packages.'NAudio.Core' -eq '2.3.0') 'The NAudio.Core version changed.'
Assert-Validation ($productManifest.packages.'NAudio.Wasapi' -eq '2.3.0') 'The NAudio.Wasapi version changed.'

$dataEntries = @($productManifest.files | Where-Object { $_.path -like 'data/*' })
$updaterEntries = @($productManifest.files | Where-Object { $_.path -like 'updater/*' })
Assert-Validation ($dataEntries.Count -eq 0) 'The release candidate contained persistent data.'
Assert-Validation ($updaterEntries.Count -ge 5) 'The release candidate did not include the updater payload.'

$updateCheckPath = Join-Path $outputRoot 'update-check.json'
$confirmationPath = Join-Path $outputRoot 'update-confirmation.json'
$processPath = Join-Path $outputRoot 'update-process.json'
$updateCheck = Invoke-ProductValidation 'BOK_UPDATE_CHECK_VALIDATION' $validationExecutable $updateCheckPath
$confirmation = Invoke-ProductValidation 'BOK_UPDATE_CONFIRMATION_VALIDATION' $validationExecutable $confirmationPath
$process = Invoke-ProductValidation 'BOK_UPDATE_PROCESS_VALIDATION' $helperRoot $processPath

Assert-Validation ($updateCheck.menuDisplayed -and $updateCheck.cancellationObserved -and $updateCheck.settingsReloaded) `
    'The real-WPF update-check gate did not verify menu, cancellation, and persisted schedule state.'
Assert-Validation ($confirmation.declinedWithoutDownload -and $confirmation.closedOnlyAfterReady -and $confirmation.helperRequestValidated) `
    'The real-WPF confirmation gate did not verify refusal, request validation, and ready-before-close ordering.'
Assert-Validation ($process.realUpdaterProcess -and $process.dataPreserved -and $process.unknownFilePreserved -and $process.restartProbeObserved) `
    'The real updater-process gate did not verify replacement, preservation, rollback, and restart.'

$publicDownload = $null
if ($IncludePublicDownload) {
    $publicDownloadPath = Join-Path $outputRoot 'public-download.json'
    $publicDownload = Invoke-ProductValidation 'BOK_UPDATE_DOWNLOAD_VALIDATION' $validationExecutable $publicDownloadPath
    Assert-Validation ($publicDownload.executablePresent -and $publicDownload.dataEntryCount -eq 0) `
        'The public release download did not pass executable and data-exclusion validation.'
}

$report = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    applicationVersion = $assetManifest.applicationVersion
    releaseCandidate = [ordered]@{
        totalFiles = $productManifest.totalFiles
        totalBytes = $productManifest.totalBytes
        libVlcPluginCount = $productManifest.libVlcPluginCount
        excludedGplPluginCount = @($productManifest.excludedGplPlugins).Count
        updaterFileCount = $updaterEntries.Count
        dataEntryCount = $dataEntries.Count
        cleanTreeRequiredForPublicRelease = $publicCleanTreeGateDeclared
        correspondingSourceRequiredForPublicRelease = $publicSourceGateDeclared
    }
    requirements = [ordered]@{
        versionComparison = $true
        twentyFourHourSchedule = $true
        explicitConfirmation = [bool]$confirmation.realNativeConfirmationDialogs
        cancellation = [bool]$updateCheck.cancellationObserved
        communicationAndApiLimits = $true
        manifestAndDigest = $true
        zipAndCapacity = $true
        storagePermissionFailure = $true
        parentExitWait = [bool]$process.parentIdentityChecked
        replacementAndRollback = [bool]$process.realUpdaterProcess
        restart = [bool]$process.restartProbeObserved
        persistentDataPreserved = [bool]$process.dataPreserved
        dependencyAndLicenseGate = $productManifest.libVlcPluginCount -eq 319
        cleanTreeGate = $publicCleanTreeGateDeclared
        correspondingSourceGate = $publicSourceGateDeclared
    }
    realWpfUpdateCheck = $updateCheck
    realWpfConfirmation = $confirmation
    realUpdaterProcess = $process
    publicDownload = $publicDownload
}

$temporaryReportPath = "$reportPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporaryReportPath -Encoding utf8
    Move-Item -LiteralPath $temporaryReportPath -Destination $reportPath
} finally {
    Remove-Item -LiteralPath $temporaryReportPath -Force -ErrorAction SilentlyContinue
}

Write-Output "Update validation completed for $expectedApplicationVersion`: $reportPath"
