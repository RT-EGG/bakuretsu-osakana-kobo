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
$publisher = Join-Path $repoRoot 'scripts\publish-phase2-release.ps1'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $outputRoot 'BakuretsuOsakanaKobo-win-x64'
$binaryArchive = Join-Path $outputRoot 'BakuretsuOsakanaKobo-win-x64.zip'
$assetManifestPath = Join-Path $outputRoot 'release-assets.json'
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

[ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    validationOnly = [bool]$ValidationOnly
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
