<#
.SYNOPSIS
    模块 12：Native AOT 发布 + 交付前冒烟门禁（三条闸门，任一失败即熔断）。

.DESCRIPTION
    产出交付目录（默认 dist/stg/bin）：UnityShaderLsp.exe + dxcompiler.dll + dxil.dll。

    闸门 1：PDB 归档到 symbols/ 子目录（不删除）。dotnet publish 是增量的，旧 PDB 会留在输出目录
            （本机实测残留 13 MB / 6 个 PDB）。保留符号用于 Native AOT 崩溃时的行号映射；
            IlcGenerateStackTraceData 已开（csproj 明确不关），栈数据有了，差的就是行号。
    闸门 2：dxcompiler.dll / dxil.dll 必须与 exe 同目录。交付形态是「同目录成对」，
            绝不从系统目录搜版本（详设 v2.0 第 2/5 条）。
    闸门 3：exe --version 必须真的加载 DXC 并打印版本。只证明「.NET 能启动」
            会掩盖 P/Invoke 延迟绑定失败（详设 v2.0 第 6 条）。

.PARAMETER Configuration
    配置，默认 Release。

.PARAMETER DxcDirectory
    含 dxcompiler.dll / dxil.dll 的源目录。默认 probes/dxc-aot/native。

.PARAMETER InstallToVsCode
    闸门通过后同步到本机 VS Code 扩展目录（防「代码改了、扩展里还是旧 exe」）。

.EXAMPLE
    pwsh -File scripts/publish-aot.ps1
.EXAMPLE
    pwsh -File scripts/publish-aot.ps1 -InstallToVsCode
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $DxcDirectory,

    # 只重跑三条闸门（不重新发布）。用于在已有产物上快速复核交付形态。
    [switch] $SkipPublish,

    # 闸门全过后把 exe 同步进本机已装的 VS Code 扩展（调用 sync-extension-bin.ps1，哈希一致则跳过）。
    [switch] $InstallToVsCode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/MicroShader.Server/MicroShader.Server.csproj'
$stage = Join-Path $root 'dist/stg/bin'
if (-not $DxcDirectory) { $DxcDirectory = Join-Path $root 'probes/dxc-aot/native' }

if (-not $SkipPublish) {
    Write-Host '== 1/5 清理暂存区（避免增量发布把旧 PDB / 旧 DLL 留在产物里）'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Write-Host '== 2/5 Native AOT 发布（-r win-x64 -p:PublishAot=true）'
    dotnet publish $project -c $Configuration -r win-x64 -p:PublishAot=true -o $stage --nologo
    if ($LASTEXITCODE -ne 0) { throw "AOT 发布失败（退出码 $LASTEXITCODE）" }
}

Write-Host '== 3/5 闸门 1：PDB 归档到 symbols/（保留行号用于崩溃诊断）'
$pdb = @(Get-ChildItem -Path $stage -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue)
if ($pdb.Count -gt 0) {
    $bytes = ($pdb | Measure-Object -Property Length -Sum).Sum
    $symbolsDir = Join-Path $stage 'symbols'
    New-Item -ItemType Directory -Path $symbolsDir -Force | Out-Null
    Write-Host ("    归档 {0} 个 PDB（{1:N2} MB）→ symbols/" -f $pdb.Count, ($bytes / 1MB)) -ForegroundColor Yellow
    $pdb | Move-Item -Destination $symbolsDir -Force
}

Write-Host '== 4/5 闸门 2：原生依赖同目录成对注入'
foreach ($name in @('dxcompiler.dll', 'dxil.dll')) {
    $source = Join-Path $DxcDirectory $name
    if (-not (Test-Path $source)) { throw "缺少原生依赖：$source" }
    Copy-Item $source (Join-Path $stage $name) -Force
    Write-Host ("   {0}  {1:N2} MB" -f $name, ((Get-Item $source).Length / 1MB))
}

$exe = Join-Path $stage 'UnityShaderLsp.exe'
if (-not (Test-Path $exe)) { throw "未产出 UnityShaderLsp.exe" }

Write-Host '== 5/5 闸门 3：AOT exe 自检（真加载 DXC）'
Write-Host ("   exe 体积 {0:N2} MB" -f ((Get-Item $exe).Length / 1MB))
$version = & $exe --version
$version | ForEach-Object { Write-Host "   $_" }
if ($LASTEXITCODE -ne 0) { throw "AOT 冒烟失败（退出码 $LASTEXITCODE）：DXC 未能加载" }

Write-Host ''
Write-Host "交付目录就绪：$stage" -ForegroundColor Green
Get-ChildItem $stage | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize

if ($InstallToVsCode) {
    Write-Host '== 附加：同步到本机 VS Code 扩展'
    & (Join-Path $PSScriptRoot 'sync-extension-bin.ps1') -Source $exe
}
