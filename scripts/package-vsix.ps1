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
$manifest = Get-Content (Join-Path $ext 'package.json') -Raw | ConvertFrom-Json
$version = $manifest.version

# ── 闸门：语言归属自肃 ────────────────────────────────────────────────────────
# 2026-10-05 踩过的坑：0.1.2 把 .hlsl / .cginc 写进了 shaderlab.extensions，
# 而 VS Code 的「扩展名 → language id」只有一格且后注册者通吃 —— 于是抢走了
# 内置 hlsl 语言的归属，导致所有只在 onLanguage:hlsl 激活的第三方扩展静默失效
# （没有报错、没有提示，表现为「HLSL 里什么补全都没有」）。
# 该声明已在 0.1.3 撤掉，这条闸门防止它被写回来。
$forbidden = @('.hlsl', '.cginc')
$claimed = @()
foreach ($lang in $manifest.contributes.languages) {
    if ($null -ne $lang.extensions) { $claimed += $lang.extensions }
}
$clash = @($claimed | Where-Object { $forbidden -contains $_ })
if ($clash.Count -gt 0) {
    throw "闸门失败：languages.extensions 不得声明 $($clash -join ' / ')。那会抢走 VS Code 内置 hlsl 语言的扩展名归属，使依赖 onLanguage:hlsl 的第三方扩展静默失效。"
}
Write-Host "  闸门通过：未抢占 .hlsl / .cginc（当前声明 $($claimed -join ' ')）"

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
