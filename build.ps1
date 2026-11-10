<#
  WenQwen 一键构建脚本  —— 温启志  18719361399

  克隆仓库后，WenQwen\bin 与 WenQwen\obj 可以完全不存在，跑本脚本会自动还原依赖并生成。

  用法:
    .\build.ps1                      构建 Debug
    .\build.ps1 -Run                 构建完直接启动
    .\build.ps1 -Clean               先删 bin/obj 再重建
    .\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Clean,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'WenQwen\WenQwen.csproj'

function Info($t) { Write-Host $t -ForegroundColor Cyan }
function Ok($t)   { Write-Host $t -ForegroundColor Green }
function Warn2($t){ Write-Host $t -ForegroundColor Yellow }

Info "== WenQwen 构建 ($Configuration) =="

if (-not (Test-Path $proj)) { Warn2 "找不到工程文件: $proj"; exit 1 }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Warn2 '未检测到 .NET SDK, 请先安装: https://dotnet.microsoft.com/download/dotnet/8.0'
    exit 1
}
Write-Host ('   SDK 版本: ' + (& dotnet --version))

$binDir = Join-Path $root 'WenQwen\bin'
$objDir = Join-Path $root 'WenQwen\obj'

if ($Clean) {
    Info '-- 清理 bin / obj'
    foreach ($d in @($binDir, $objDir)) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
    }
    Ok '   已清理'
}

Info '-- 还原依赖并编译'
& dotnet build $proj -c $Configuration -nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { Warn2 "构建失败 (exit $LASTEXITCODE)"; exit $LASTEXITCODE }

$outDir = Join-Path $binDir "$Configuration\net8.0-windows"
if (-not (Test-Path (Join-Path $outDir 'WenQwen.exe'))) {
    $cand = Get-ChildItem -Path (Join-Path $binDir $Configuration) -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'WenQwen.exe') } | Select-Object -First 1
    if ($cand) { $outDir = $cand.FullName }
}
$exe = Join-Path $outDir 'WenQwen.exe'
if (-not (Test-Path $exe)) { Warn2 '没有找到 WenQwen.exe, 请检查上面的编译输出'; exit 1 }

$files = Get-ChildItem -Recurse -File $outDir
$mb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
Ok '== 构建完成 =='
Write-Host "   可执行: $exe"
Write-Host "   产物目录: $outDir"
Write-Host "   文件数: $($files.Count) 个, 共 $mb MB"

$docs = Join-Path $outDir '资料'
if (-not (Test-Path $docs)) {
    New-Item -ItemType Directory -Path $docs -Force | Out-Null
    Write-Host "   已创建知识库目录: $docs"
}

if ($Run) {
    Info '-- 启动 WenQwen'
    Start-Process -FilePath $exe -WorkingDirectory $outDir
}
