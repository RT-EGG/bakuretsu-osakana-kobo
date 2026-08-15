[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$CorrespondingSourceArchive,

    [switch]$ValidationOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'BakuretsuOsakanaKobo.slnx'
$project = Join-Path $repoRoot 'src\BakuretsuOsakanaKobo.App\BakuretsuOsakanaKobo.App.csproj'
$buildProperties = Join-Path $repoRoot 'Directory.Build.props'
$publisher = Join-Path $repoRoot 'scripts\publish-phase2-release.ps1'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $outputRoot 'BakuretsuOsakanaKobo-win-x64'
$binaryArchive = Join-Path $outputRoot 'BakuretsuOsakanaKobo-win-x64.zip'
$assetManifestPath = Join-Path $outputRoot 'release-assets.json'
$buildPropertiesDocument = [xml](Get-Content -Raw -LiteralPath $buildProperties)
$applicationVersion = [string]$buildPropertiesDocument.Project.PropertyGroup.VersionPrefix
if ($applicationVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must contain a three-part VersionPrefix: $applicationVersion"
}
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'The release source commit could not be resolved.'
}
$sourceTreeDirty = @(& git -C $repoRoot status --porcelain).Count -ne 0
if (-not $ValidationOnly -and $sourceTreeDirty) {
    throw 'A public release candidate must be generated from a clean worktree.'
}
$sourceArchivePath = if ([string]::IsNullOrWhiteSpace($CorrespondingSourceArchive)) {
    if (-not $ValidationOnly) {
        throw 'A complete corresponding-source archive is mandatory for a public release.'
    }

    $null
} else {
    (Resolve-Path -LiteralPath $CorrespondingSourceArchive).Path
}

if (Test-Path -LiteralPath $outputRoot) {
    if (@(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ne 0) {
        throw "Output directory must be absent or empty: $outputRoot"
    }
} else {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
}

& dotnet restore $solution --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

& dotnet build $solution --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

& dotnet test $solution --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

$publishArguments = @{
    ProjectPath = $project
    OutputDirectory = $publishDirectory
    DistributionMode = 'FrameworkDependent'
    ValidationOnly = $ValidationOnly
}
if ($sourceArchivePath) {
    $publishArguments.CorrespondingSourceArchive = $sourceArchivePath
}

& $publisher @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Release publisher failed with exit code $LASTEXITCODE." }

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $binaryArchive -CompressionLevel Optimal

$publishedExecutable = Join-Path $publishDirectory 'BakuretsuOsakanaKobo.exe'
$publishedVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo
$expectedFileVersion = "$applicationVersion.0"
if ($publishedVersion.FileVersion -ne $expectedFileVersion) {
    throw "Published executable version mismatch. Expected $expectedFileVersion, actual $($publishedVersion.FileVersion)."
}

[ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    validationOnly = [bool]$ValidationOnly
    applicationVersion = $applicationVersion
    sourceCommit = $sourceCommit
    sourceTreeDirty = $sourceTreeDirty
    executable = [ordered]@{
        fileVersion = $publishedVersion.FileVersion
        productVersion = $publishedVersion.ProductVersion
    }
    binaryArchive = [ordered]@{
        file = [IO.Path]::GetFileName($binaryArchive)
        bytes = (Get-Item -LiteralPath $binaryArchive).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $binaryArchive).Hash
    }
    correspondingSourceArchive = if ($sourceArchivePath) {
        [ordered]@{
            file = [IO.Path]::GetFileName($sourceArchivePath)
            bytes = (Get-Item -LiteralPath $sourceArchivePath).Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceArchivePath).Hash
        }
    } else {
        $null
    }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $assetManifestPath -Encoding utf8

Write-Output "Release candidate completed: $outputRoot"
