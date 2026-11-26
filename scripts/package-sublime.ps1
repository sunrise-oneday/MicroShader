<#
.SYNOPSIS
    把 MicroShader 打成 Sublime Text 的 LSP helper 包 —— 对位 VS Code 的 package-vsix.ps1。

.DESCRIPTION
    产出一个自包含的 dist/LSP-MicroShader.sublime-package（本质是 zip），内含：
      LSP-MicroShader.sublime-settings  服务器配置（command 用 $storage_path 模板，无硬编码路径）
      plugin.py                         LspPlugin 子类：包加载时把二进制释放到 Package Storage
      shaderlab.sublime-syntax          语法（提供 source.shaderlab scope，selector 靠它匹配）
      Default.sublime-keymap            F12 定义跳转 / Ctrl+Alt+Enter 跟随 include
      bin/UnityShaderLsp.exe            服务端本体
      bin/dxcompiler.dll
      bin/dxil.dll

    安装 = 把这一个文件放进 Installed Packages/，重启 Sublime 即可。
    二进制由 plugin.py 在首次加载时释放到 <Package Storage>/LSP-MicroShader/bin/，
    且 -Install 会预先放好一份（不依赖插件宿主的执行）；
    Windows 上 Package Storage 在 %LOCALAPPDATA%\Sublime Text\（不是 %APPDATA%），
    因此包里没有任何绝对路径，用户也不需要手工改配置。

.EXAMPLE
    pwsh -File scripts/package-sublime.ps1 -Install
#>
[CmdletBinding()]
param(
    [string] $SublimeDir,
    [string] $DataDir,
    [switch] $Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Info($m) { Write-Host "  $m" }
function Step($m) { Write-Host "== $m" }

$PACKAGE = 'LSP-MicroShader'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'client/sublime'

# ---------- 1. 定位服务端二进制 ----------
Step '1/3 收集文件'

$stageBin = Join-Path $root 'dist/stg/bin'
$bundledBin = Join-Path $root 'client/vscode/bin'
$serverDir = if (Test-Path (Join-Path $stageBin 'UnityShaderLsp.exe')) { $stageBin } else { $bundledBin }

$binaries = @('UnityShaderLsp.exe', 'dxcompiler.dll')
foreach ($name in $binaries) {
    if (-not (Test-Path (Join-Path $serverDir $name))) {
        throw "缺少 $serverDir\$name。先跑 scripts/publish-aot.ps1 或 scripts/package-vsix.ps1。"
    }
}
$optional = @('dxil.dll') | Where-Object { Test-Path (Join-Path $serverDir $_) }
Info "服务端来源 $serverDir"

# ---------- 2. 暂存并压缩 ----------
Step '2/3 构建 sublime-package'

$stage = Join-Path $env:TEMP "$PACKAGE-stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$stageBinDir = Join-Path $stage 'bin'
New-Item -ItemType Directory -Path $stageBinDir -Force | Out-Null

foreach ($name in @("$PACKAGE.sublime-settings", 'plugin.py', 'shaderlab.sublime-syntax', 'Default.sublime-keymap')) {
    $from = Join-Path $src $name
    if (-not (Test-Path $from)) { throw "缺少 $from" }
    Copy-Item $from (Join-Path $stage $name) -Force
}
foreach ($name in ($binaries + $optional)) {
    Copy-Item (Join-Path $serverDir $name) (Join-Path $stageBinDir $name) -Force
}

$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$out = Join-Path $outDir "$PACKAGE.sublime-package"
if (Test-Path $out) { Remove-Item $out -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
# includeBaseDirectory = $false：zip 根就是包根，这是 .sublime-package 的硬要求
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $out, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$size = (Get-Item $out).Length
Info ("{0}  {1:N2} MB" -f $out, ($size / 1MB))

# ---------- 3. 可选安装 ----------
if (-not $Install) {
    Write-Host ''
    Write-Host '安装方式：把这个文件放进 Installed Packages/，重启 Sublime。'
    Write-Host '或加 -Install 让本脚本代劳。'
    return
}

Step '3/3 安装到 Sublime'

if (-not $SublimeDir) {
    $candidates = @()
    $fromPath = Get-Command subl.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates += (Split-Path $fromPath.Source) }
    $candidates += @(
        'E:\Vs_Code\Sublime Text',
        (Join-Path $env:LOCALAPPDATA 'Programs\Sublime Text'),
        (Join-Path $env:ProgramFiles 'Sublime Text'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Sublime Text')
    )
    $SublimeDir = $candidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'sublime_text.exe')) } | Select-Object -First 1
}
if (-not $SublimeDir) { throw '找不到 Sublime Text 安装目录，请用 -SublimeDir 指定' }

$isPortable = $false
if (-not $DataDir) {
    $portable = Join-Path $SublimeDir 'Data'
    if (Test-Path $portable) { $DataDir = $portable; $isPortable = $true }
    else { $DataDir = Join-Path $env:APPDATA 'Sublime Text' }
} else {
    $isPortable = (Split-Path -Leaf $DataDir) -eq 'Data'
}

$installedDir = Join-Path $DataDir 'Installed Packages'
$legacyDir = Join-Path $DataDir "Packages/$PACKAGE"

if (-not (Test-Path $installedDir)) {
    # Sublime 没启动过时数据目录不存在 —— 数据目录缺失说明它一次都没跑起来
    throw "找不到 $installedDir。先启动一次 Sublime Text，再用 -Install。"
}

# 如果之前用「解压成目录」的方式装过同名包，zip 版本不会覆盖它，要先清掉
if (Test-Path $legacyDir) {
    Write-Host "  ! 检测到解压形态的 $legacyDir，它会和 sublime-package 冲突。请手工删除后重试。" -ForegroundColor Yellow
    exit 3
}

Copy-Item $out (Join-Path $installedDir "$PACKAGE.sublime-package") -Force
Info "已安装到 $installedDir\$PACKAGE.sublime-package"

# 预置服务端二进制到 Package Storage。
# plugin.py 也会做这件事，这里再做一遍是为了让 -Install 自成闭环：
# 即使插件宿主因故没跑起来（或首次启动被中断），配置里的路径也已经存在。
# Package Storage 的位置来自 LSP 的 ST_STORAGE_PATH = dirname(cache_path)/"Package Storage"：
#   便携版 -> <安装目录>\Data\Package Storage
#   普通版 -> %LOCALAPPDATA%\Sublime Text\Package Storage（注意不是 %APPDATA%）
$storageRoot = if ($isPortable) {
    Join-Path $DataDir 'Package Storage'
} else {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Sublime Text/Package Storage'
}
$storageBin = Join-Path $storageRoot "$PACKAGE/bin"
New-Item -ItemType Directory -Path $storageBin -Force | Out-Null
foreach ($name in ($binaries + $optional)) {
    Copy-Item (Join-Path $serverDir $name) (Join-Path $storageBin $name) -Force
}
Info "已预置二进制到 $storageBin"

Write-Host ''
Write-Host '下一步：' -ForegroundColor Green
Write-Host '  1. 完全退出并重开 Sublime Text（不是只关窗口 —— 插件宿主只在启动时加载包）。'
Write-Host '  2. 打开一个 .shader 文件。'
Write-Host '  3. 状态栏左侧应出现 MicroShader；F12 跳定义，Ctrl+Alt+Enter 跟随 #include。'
Write-Host '  4. 若没出现：Ctrl+Shift+P -> LSP: Troubleshoot Server。'
Write-Host ''
Write-Host ('  服务端二进制位于：' + $storageBin)
