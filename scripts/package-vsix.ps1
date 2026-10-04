<#
.SYNOPSIS
    打包 VS Code 扩展为 vsix（可选直接安装），替代「手工复制 exe/extension.js 进扩展目录」。

.DESCRIPTION
    1. 从 dist/stg/bin 拷入 UnityShaderLsp.exe + dxcompiler.dll + dxil.dll 到 client/vscode/bin
       （.vscodeignore 只放行这三个文件；PDB 不进 vsix）。
    2. npm run compile（esbuild 产出 dist/extension.js）。
    3. vsce package → dist/microshader-unity-shader-lsp-<version>.vsix。
    4. -Install：code --install-extension --force（带 __metadata，可正常升级/卸载/回滚）。
    版本号取自 client/vscode/package.json；重复安装同版本前请先 bump。

.EXAMPLE
    pwsh -File scripts/package-vsix.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch] $Install,
    # VS Code CLI；缺省先查 PATH，再查本机安装位置。
    [string] $Code
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$ext = Join-Path $root 'client/vscode'
$stage = Join-Path $root 'dist/stg/bin'
$version = (Get-Content (Join-Path $ext 'package.json') -Raw | ConvertFrom-Json).version

Write-Host '== 1/4 注入交付二进制'
$bin = Join-Path $ext 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
foreach ($name in @('UnityShaderLsp.exe', 'dxcompiler.dll', 'dxil.dll')) {
    $src = Join-Path $stage $name
    if (-not (Test-Path $src)) { throw "缺少 $src（先跑 scripts/publish-aot.ps1）" }
    Copy-Item $src (Join-Path $bin $name) -Force
}
$hash = (Get-FileHash (Join-Path $bin 'UnityShaderLsp.exe') -Algorithm SHA256).Hash
Write-Host "   exe SHA-256 $hash"

Push-Location $ext
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '== 安装依赖（npm ci）'
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci 失败' }
    }
    Write-Host '== 2/4 编译扩展'
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw '扩展编译失败' }

    Write-Host '== 3/4 打包 vsix'
    $outDir = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $vsix = Join-Path $outDir "microshader-unity-shader-lsp-$version.vsix"
    npx --no-install vsce package --no-dependencies -o $vsix
    if ($LASTEXITCODE -ne 0) { throw 'vsce package 失败' }
} finally { Pop-Location }

Write-Host ("   {0}  {1:N2} MB" -f $vsix, ((Get-Item $vsix).Length / 1MB))

if ($Install) {
    Write-Host '== 4/4 安装到 VS Code'
    if (-not $Code) {
        $Code = (Get-Command code.cmd -ErrorAction SilentlyContinue)?.Source
        if (-not $Code) {
            $Code = @('E:\Vs_Code\Microsoft VS Code\bin\code.cmd',
                      (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd')) |
                Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $Code) { throw '找不到 VS Code CLI（code.cmd），请用 -Code 指定' }
    }
    & $Code --install-extension $vsix --force
    if ($LASTEXITCODE -ne 0) { throw "安装失败（退出码 $LASTEXITCODE）" }
    Write-Host '生效方式：重新加载 VS Code 窗口。' -ForegroundColor Green
}
