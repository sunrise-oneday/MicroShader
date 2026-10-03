<#
.SYNOPSIS
    把交付的 UnityShaderLsp.exe 同步进本机已安装的 VS Code 扩展目录，并校验哈希。

.DESCRIPTION
    存在理由（2026-10-01 实测事故）：模块 11B 完成后，dist/stg/bin 里已经是带桥接的新 exe，
    但 ~/.vscode/extensions/<扩展>/bin 里仍是几小时前的旧 exe —— 于是「代码写完了、功能却没生效」，
    且症状是「Output 面板里什么都没有」，属于最难归因的一类。
    本脚本把这一步变成一条命令 + 一次哈希比对：哈希一致就什么都不做（幂等）。

.PARAMETER Source
    源 exe，默认 dist/stg/bin/UnityShaderLsp.exe。

.PARAMETER ExtensionsDir
    VS Code 扩展根目录，默认 %USERPROFILE%\.vscode\extensions。

.PARAMETER Force
    哈希一致时也强制复制（默认跳过）。

.EXAMPLE
    pwsh -File scripts/sync-extension-bin.ps1
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $ExtensionsDir,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'dist/stg/bin/UnityShaderLsp.exe' }
if (-not $ExtensionsDir) { $ExtensionsDir = Join-Path $env:USERPROFILE '.vscode/extensions' }

if (-not (Test-Path $Source)) { throw "源 exe 不存在：$Source（先跑 scripts/publish-aot.ps1）" }
$sourceHash = (Get-FileHash $Source -Algorithm SHA256).Hash
$sourceSize = (Get-Item $Source).Length

$candidates = @(Get-ChildItem $ExtensionsDir -Directory -Filter 'microshader.unity-shader-lsp-*' -ErrorAction SilentlyContinue)
if ($candidates.Count -eq 0) { throw "在 $ExtensionsDir 下找不到 microshader.unity-shader-lsp-*（扩展未安装？）" }

Write-Host ("源：{0}" -f $Source)
Write-Host ("    {0:N0} 字节  SHA-256 {1}" -f $sourceSize, $sourceHash) -ForegroundColor Cyan

$changed = 0
foreach ($ext in $candidates) {
    $target = Join-Path $ext.FullName 'bin/UnityShaderLsp.exe'
    if (-not (Test-Path $target)) {
        Write-Warning ("跳过（该扩展没有 bin/UnityShaderLsp.exe）：{0}" -f $ext.FullName)
        continue
    }

    $targetHash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($targetHash -eq $sourceHash -and -not $Force) {
        Write-Host ("已是最新：{0}" -f $ext.Name) -ForegroundColor Green
        continue
    }

    Write-Host ("需要更新：{0}" -f $ext.Name) -ForegroundColor Yellow
    Write-Host ("    旧 {0:N0} 字节  SHA-256 {1}" -f (Get-Item $target).Length, $targetHash)

    # exe 被占用时复制会失败：只请走「从这个扩展目录启动的」语言服务器，
    # 不误杀其他 VS Code 窗口 / 开发构建里的同名进程（父进程会按需重启它）。
    Get-Process UnityShaderLsp -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, (Resolve-Path $target).Path, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 500

    $backup = "$target.bak-sync"
    Copy-Item $target $backup -Force
    Copy-Item $Source $target -Force

    $newHash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($newHash -ne $sourceHash) { throw "复制后哈希不一致：$target" }
    $changed++
    Write-Host ("    新 {0:N0} 字节  SHA-256 {1}" -f (Get-Item $target).Length, $newHash) -ForegroundColor Green
    Write-Host ("    备份：{0}" -f $backup)
}

Write-Host ''
if ($changed -eq 0) {
    Write-Host '同步完成：无需变更。' -ForegroundColor Green
} else {
    Write-Host ("同步完成：更新了 {0} 个扩展目录。" -f $changed) -ForegroundColor Green
    Write-Host '生效方式：VS Code 执行「MicroShader: 重启语言服务器」，或重新打开窗口。'
}
