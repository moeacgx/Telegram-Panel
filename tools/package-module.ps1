param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Manifest,

    [Parameter(Mandatory = $false)]
    [string]$OutDir = "artifacts/modules",

    # 轻量打包：剔除会由宿主共享加载的程序集（减少包体积）。
    # 说明：宿主的 ModuleLoadContext 会强制让这些程序集由 Default ALC 解析，模块包携带它们只会徒增体积。
    [Parameter(Mandatory = $false)]
    [switch]$Slim,

    # 更激进的轻量打包：额外剔除宿主已内置的第三方依赖（EFCore/Sqlite/WTelegramClient 等）与多平台 native runtimes。
    # 适用场景：目标宿主就是 TelegramPanel 主程序（必带这些依赖），并且部署环境固定（例如 Docker linux-x64）。
    [Parameter(Mandatory = $false)]
    [switch]$SlimHost
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
$entryAssembly = ""
try {
    if ($null -ne $manifestObj.entry -and $null -ne $manifestObj.entry.assembly) { $entryAssembly = [string]$manifestObj.entry.assembly }
} catch { }
$entryAssembly = [string]$entryAssembly
$entryAssembly = $entryAssembly.Trim()
if ([string]::IsNullOrWhiteSpace($entryAssembly)) { throw "manifest.json 缺少 entry.assembly" }

$buildRootRel = "artifacts/_modulebuild/$moduleId/$version"
$runId = ([Guid]::NewGuid().ToString("N"))
$publishRel = "$buildRootRel/publish/$runId"
$stagingRel = "$buildRootRel/staging/$runId"

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
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码：$LASTEXITCODE）" }

$stagingLib = Join-Path $stagingHost "lib"
New-Item -ItemType Directory -Force -Path $stagingLib | Out-Null

Copy-Item -Path $manifestPath -Destination (Join-Path $stagingHost "manifest.json") -Force
Copy-Item -Path (Join-Path $publishHost "*") -Destination $stagingLib -Recurse -Force

if ($Slim -or $SlimHost)
{
    # 与宿主共享的“边界程序集”（见 src/TelegramPanel.Web/Modules/ModuleLoadContext.cs）。
    # 模块包携带这些 dll 会导致体积膨胀（同时也容易造成类型身份不一致的风险），因此在打包阶段直接剔除。
    $sharedPatterns = @(
        "Microsoft.AspNetCore.*.dll",
        "Microsoft.Extensions.*.dll",
        "Microsoft.JSInterop*.dll",
        "TelegramPanel.*.dll",
        "MudBlazor*.dll"
    )

    foreach ($pattern in $sharedPatterns)
    {
        Get-ChildItem -Path $stagingLib -Filter $pattern -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -ieq $entryAssembly) { return }
            try { Remove-Item -Path $_.FullName -Force -ErrorAction Stop } catch { }
        }

        $pdbPattern = [System.IO.Path]::ChangeExtension($pattern, ".pdb")
        Get-ChildItem -Path $stagingLib -Filter $pdbPattern -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -Path $_.FullName -Force -ErrorAction Stop } catch { }
        }
    }
}

if ($SlimHost)
{
    # 宿主已内置且通常在启动早期加载的依赖：把这些也剔除可大幅减小体积（尤其是 SQLitePCL 的多平台 runtimes/native）。
    $hostBuiltInPatterns = @(
        "Microsoft.EntityFrameworkCore*.dll",
        "Microsoft.Data.Sqlite*.dll",
        "SQLitePCLRaw*.dll",
        "WTelegramClient*.dll",
        "SixLabors.ImageSharp*.dll",
        "PhoneNumbers*.dll"
    )

    foreach ($pattern in $hostBuiltInPatterns)
    {
        Get-ChildItem -Path $stagingLib -Filter $pattern -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -ieq $entryAssembly) { return }
            try { Remove-Item -Path $_.FullName -Force -ErrorAction Stop } catch { }
        }

        $pdbPattern = [System.IO.Path]::ChangeExtension($pattern, ".pdb")
        Get-ChildItem -Path $stagingLib -Filter $pdbPattern -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -Path $_.FullName -Force -ErrorAction Stop } catch { }
        }
    }

    # 多平台 native runtimes（SQLite）体积很大；TelegramPanel 宿主自身已携带，这里直接剔除。
    $runtimesDir = Join-Path $stagingLib "runtimes"
    if (Test-Path -Path $runtimesDir)
    {
        try { Remove-Item -Path $runtimesDir -Recurse -Force -ErrorAction Stop } catch { }
    }

    # MudBlazor 静态资源由宿主提供；模块包里携带的这份没有实际用途，顺手剔除。
    $mudWwwroot = Join-Path $stagingLib "wwwroot/_content/MudBlazor"
    if (Test-Path -Path $mudWwwroot)
    {
        try { Remove-Item -Path $mudWwwroot -Recurse -Force -ErrorAction Stop } catch { }
    }
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$dest = Join-Path $outRoot "$moduleId-$version.tpm"
if (Test-Path -Path $dest) { Remove-Item -Path $dest -Force }

$destZip = [System.IO.Path]::ChangeExtension($dest, ".zip")
if (Test-Path -Path $destZip) { Remove-Item -Path $destZip -Force }

Compress-Archive -Path (Join-Path $stagingHost "*") -DestinationPath $destZip -Force
Move-Item -Path $destZip -Destination $dest -Force

Write-Host "OK: $dest" -ForegroundColor Green
