param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Manifest,

    [Parameter(Mandatory = $false)]
    [string]$OutDir = "artifacts/modules"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path ".").Path
$projectPath = Join-Path $repoRoot $Project
$manifestPath = Join-Path $repoRoot $Manifest
$outRoot = Join-Path $repoRoot $OutDir

if (-not (Test-Path -Path $projectPath)) { throw "Project 不存在：$Project" }
if (-not (Test-Path -Path $manifestPath)) { throw "Manifest 不存在：$Manifest" }

$manifestObj = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$moduleId = ""
$version = ""
if ($null -ne $manifestObj.id) { $moduleId = [string]$manifestObj.id }
if ($null -ne $manifestObj.version) { $version = [string]$manifestObj.version }
$moduleId = $moduleId.Trim()
$version = $version.Trim()
if ([string]::IsNullOrWhiteSpace($moduleId)) { throw "manifest.json 缺少 id" }
if ([string]::IsNullOrWhiteSpace($version)) { throw "manifest.json 缺少 version" }

$buildRootRel = "artifacts/_modulebuild/$moduleId/$version"
$publishRel = "$buildRootRel/publish"
$stagingRel = "$buildRootRel/staging"

$publishHost = Join-Path $repoRoot $publishRel
$stagingHost = Join-Path $repoRoot $stagingRel

New-Item -ItemType Directory -Force -Path $publishHost | Out-Null
New-Item -ItemType Directory -Force -Path $stagingHost | Out-Null

$publishContainer = "/src/$publishRel"
$projectContainer = "/src/$Project"

Write-Host "Building module with Docker..." -ForegroundColor Cyan
docker run --rm `
    -v "${repoRoot}:/src" `
    -w "/src" `
    mcr.microsoft.com/dotnet/sdk:8.0 `
    dotnet publish "$projectContainer" -c Release -o "$publishContainer" /p:UseAppHost=false

$stagingLib = Join-Path $stagingHost "lib"
New-Item -ItemType Directory -Force -Path $stagingLib | Out-Null

Copy-Item -Path $manifestPath -Destination (Join-Path $stagingHost "manifest.json") -Force
Copy-Item -Path (Join-Path $publishHost "*") -Destination $stagingLib -Recurse -Force

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$dest = Join-Path $outRoot "$moduleId-$version.tpm"
if (Test-Path -Path $dest) { Remove-Item -Path $dest -Force }

$destZip = [System.IO.Path]::ChangeExtension($dest, ".zip")
if (Test-Path -Path $destZip) { Remove-Item -Path $destZip -Force }

Compress-Archive -Path (Join-Path $stagingHost "*") -DestinationPath $destZip -Force
Move-Item -Path $destZip -Destination $dest -Force

Write-Host "OK: $dest" -ForegroundColor Green
