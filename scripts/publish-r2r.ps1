<#
.SYNOPSIS
    开发/自检用 R2R（ReadyToRun）发布脚本。

.DESCRIPTION
    产出 dist/r2r/bin：UnityShaderLsp.exe + dxcompiler.dll + dxil.dll。
    与 publish-aot.ps1 的区别：
      - 不开 PublishAot（产物是 JIT + R2R 预编译，不是 Native AOT）
      - 自包含单文件（self-contained, single-file）
      - 天然带 PDB，可附加调试器
      - 体积更大（~70MB），启动稍慢（~200-300ms vs AOT 108ms）
    用途：开发期自检、性能基准（让数字反映产品 AOT 形态而非纯 JIT）、
          COM 互操作迁移时的调试（有完整托管栈）。

.EXAMPLE
    pwsh -File scripts/publish-r2r.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $DxcDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/MicroShader.Server/MicroShader.Server.csproj'
$stage = Join-Path $root 'dist/r2r/bin'
if (-not $DxcDirectory) { $DxcDirectory = Join-Path $root 'probes/dxc-aot/native' }

Write-Host '== 1/3 清理暂存区'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host '== 2/3 R2R 发布（self-contained + single-file + ReadyToRun）'
dotnet publish $project -c $Configuration -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "R2R 发布失败（退出码 $LASTEXITCODE）" }

Write-Host '== 3/3 原生依赖同目录成对注入 + 冒烟'
foreach ($name in @('dxcompiler.dll', 'dxil.dll')) {
    $source = Join-Path $DxcDirectory $name
    if (-not (Test-Path $source)) { throw "缺少原生依赖：$source" }
    Copy-Item $source (Join-Path $stage $name) -Force
    Write-Host ("   {0}  {1:N2} MB" -f $name, ((Get-Item $source).Length / 1MB))
}

$exe = Join-Path $stage 'UnityShaderLsp.exe'
if (-not (Test-Path $exe)) { throw "未产出 UnityShaderLsp.exe" }

Write-Host ("   exe 体积 {0:N2} MB" -f ((Get-Item $exe).Length / 1MB))
$version = & $exe --version
$version | ForEach-Object { Write-Host "   $_" }
if ($LASTEXITCODE -ne 0) { throw "R2R 冒烟失败（退出码 $LASTEXITCODE）：DXC 未能加载" }

Write-Host ''
Write-Host "开发/自检目录就绪：$stage" -ForegroundColor Green
Get-ChildItem $stage | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
