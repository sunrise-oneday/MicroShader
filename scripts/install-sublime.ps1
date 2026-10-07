<#
.SYNOPSIS
    把 MicroShader 接入 Sublime Text 4 的 LSP 包（一条命令，对位 VS Code 的 package-vsix.ps1 -Install）。

.DESCRIPTION
    1. 定位 Sublime 数据目录（便携版 <安装目录>\Data 优先，否则 %APPDATA%\Sublime Text）
    2. 检查 LSP 包是否已安装（没装就给安装指引并退出）
    3. 把 microshader 这一项合并进 Packages/User/LanguageServers.sublime-settings
       （只替换 microshader 自己那段，其它服务器配置与注释原样保留）
    4. 复制 shaderlab.sublime-syntax 与 Default.sublime-keymap 到 Packages/User

.EXAMPLE
    pwsh -File scripts/install-sublime.ps1 -ProjectPath 'D:\MyUnityProject'

.NOTES
    幂等：重复执行只会把 microshader 那段刷新成当前路径。
#>
[CmdletBinding()]
param(
    [string] $SublimeDir,
    [string] $DataDir,
    [string] $ProjectPath,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$C_LBRACE = [int][char]'{'
$C_RBRACE = [int][char]'}'
$C_LBRACKET = [int][char]'['
$C_RBRACKET = [int][char]']'
$C_QUOTE = [int][char]'"'
$C_SLASH = [int][char]'/'
$C_STAR = [int][char]'*'
$C_BACKSLASH = [int][char]'\'
$C_LF = 10

function Info($m) { Write-Host "  $m" }
function Step($m) { Write-Host "== $m" }

# 找顶层 "Key" 对应的对象字面量起止下标（跳过字符串与注释）。
# 找不到或结构异常时返回 $null —— 调用方据此退化为「追加」，而不是猜着改用户文件。
function Get-TopLevelObjectRange {
    param([string] $Text, [string] $Key)

    $n = $Text.Length
    $i = 0
    $depth = 0
    $inStr = $false
    $lineComment = $false
    $blockComment = $false

    while ($i -lt $n) {
        $c = [int]$Text[$i]

        if ($lineComment) { if ($c -eq $C_LF) { $lineComment = $false }; $i++; continue }
        if ($blockComment) {
            if ($c -eq $C_STAR -and ($i + 1) -lt $n -and [int]$Text[$i + 1] -eq $C_SLASH) { $blockComment = $false; $i += 2; continue }
            $i++; continue
        }
        if ($inStr) {
            if ($c -eq $C_BACKSLASH) { $i += 2; continue }
            if ($c -eq $C_QUOTE) { $inStr = $false }
            $i++; continue
        }
        if ($c -eq $C_SLASH -and ($i + 1) -lt $n) {
            if ([int]$Text[$i + 1] -eq $C_SLASH) { $lineComment = $true; $i += 2; continue }
            if ([int]$Text[$i + 1] -eq $C_STAR) { $blockComment = $true; $i += 2; continue }
        }
        if ($c -eq $C_QUOTE) {
            if ($depth -eq 1) {
                $j = $i + 1
                while ($j -lt $n -and [int]$Text[$j] -ne $C_QUOTE) { $j++ }
                $name = $Text.Substring($i + 1, $j - $i - 1)
                if ($name -eq $Key) {
                    $k = $j + 1
                    while ($k -lt $n -and [int]$Text[$k] -ne $C_LBRACE) { $k++ }
                    if ($k -ge $n) { return $null }
                    $d = 0
                    $m = $k
                    $s2 = $false; $l2 = $false; $b2 = $false
                    while ($m -lt $n) {
                        $cc = [int]$Text[$m]
                        if ($l2) { if ($cc -eq $C_LF) { $l2 = $false }; $m++; continue }
                        if ($b2) {
                            if ($cc -eq $C_STAR -and ($m + 1) -lt $n -and [int]$Text[$m + 1] -eq $C_SLASH) { $b2 = $false; $m += 2; continue }
                            $m++; continue
                        }
                        if ($s2) {
                            if ($cc -eq $C_BACKSLASH) { $m += 2; continue }
                            if ($cc -eq $C_QUOTE) { $s2 = $false }
                            $m++; continue
                        }
                        if ($cc -eq $C_SLASH -and ($m + 1) -lt $n) {
                            if ([int]$Text[$m + 1] -eq $C_SLASH) { $l2 = $true; $m += 2; continue }
                            if ([int]$Text[$m + 1] -eq $C_STAR) { $b2 = $true; $m += 2; continue }
                        }
                        if ($cc -eq $C_QUOTE) { $s2 = $true; $m++; continue }
                        if ($cc -eq $C_LBRACE) { $d++; $m++; continue }
                        if ($cc -eq $C_RBRACE) {
                            $d--
                            $m++
                            if ($d -eq 0) { return [pscustomobject]@{ Start = $i; End = $m - 1 } }
                            continue
                        }
                        $m++
                    }
                    return $null
                }
                $i = $j + 1
                continue
            }
            $j = $i + 1
            while ($j -lt $n -and [int]$Text[$j] -ne $C_QUOTE) {
                if ([int]$Text[$j] -eq $C_BACKSLASH) { $j++ }
                $j++
            }
            $i = $j + 1
            continue
        }
        if ($c -eq $C_LBRACE -or $c -eq $C_LBRACKET) { $depth++; $i++; continue }
        if ($c -eq $C_RBRACE -or $c -eq $C_RBRACKET) { $depth--; $i++; continue }
        $i++
    }
    return $null
}

# ---------- 1. 定位 Sublime ----------
Step '1/4 定位 Sublime Text'

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
Info "安装目录 $SublimeDir"

if (-not $DataDir) {
    $portable = Join-Path $SublimeDir 'Data'
    $DataDir = if (Test-Path $portable) { $portable } else { Join-Path $env:APPDATA 'Sublime Text' }
}
Info "数据目录 $DataDir"

$packages = Join-Path $DataDir 'Packages'
$userDir = Join-Path $packages 'User'

# ---------- 2. 检查 LSP 包 ----------
Step '2/4 检查 LSP 包'

$lspInstalled = (Test-Path (Join-Path $DataDir 'Installed Packages/LSP.sublime-package')) -or (Test-Path (Join-Path $packages 'LSP'))
if (-not $lspInstalled) {
    Write-Host ''
    Write-Host '  [x] 没有检测到 LSP 包 —— 服务端配置写了也不会被加载。' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  先装 LSP，任选一种：'
    Write-Host '    A) 有 Package Control:' -ForegroundColor Cyan
    Write-Host '       Ctrl+Shift+P -> Package Control: Install Package -> 搜 LSP'
    Write-Host '    B) 手动（无 Package Control 时）:' -ForegroundColor Cyan
    Write-Host '       到 https://github.com/sublimelsp/LSP/releases 下载 LSP.sublime-package,'
    Write-Host "       放进 $DataDir\Installed Packages\"
    Write-Host ''
    Write-Host '  装完重跑本脚本即可。'
    exit 2
}
Info 'LSP 包已安装'

# 与 helper 包路线互斥：两条路都写会产生两个不同名的服务器配置（microshader / MicroShader），
# LSP 会同时启动两份会话 —— 表现为诊断和补全都出现重复，很难排查。
$helperZip = Join-Path $DataDir 'Installed Packages/LSP-MicroShader.sublime-package'
$helperDir = Join-Path $packages 'LSP-MicroShader'
if ((Test-Path $helperZip) -or (Test-Path $helperDir)) {
    Write-Host ''
    Write-Host '  [x] 检测到已安装 LSP-MicroShader 包 —— 本脚本是「User 配置」那条路，两者互斥。' -ForegroundColor Yellow
    Write-Host '      同时安装会让 LSP 启动两份服务器会话（配置名 microshader 与 MicroShader 不同），'
    Write-Host '      表现为诊断与补全成对重复出现。'
    Write-Host ''
    Write-Host '      只想用包：不用再跑本脚本，重启 Sublime 即可。'
    Write-Host '      只想用本脚本：先删掉 Installed Packages\LSP-MicroShader.sublime-package 再重跑。'
    exit 3
}

# ---------- 3. 定位服务端二进制 ----------
Step '3/4 定位服务端二进制'

$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'dist/stg/bin'
$bundled = Join-Path $root 'client/vscode/bin'
$serverDir = if (Test-Path (Join-Path $stage 'UnityShaderLsp.exe')) { $stage } else { $bundled }
$exe = Join-Path $serverDir 'UnityShaderLsp.exe'

if (-not (Test-Path $exe)) {
    throw "找不到 UnityShaderLsp.exe（找过 $stage 与 $bundled）。先跑 scripts/publish-aot.ps1 或 scripts/package-vsix.ps1。"
}
if (-not (Test-Path (Join-Path $serverDir 'dxcompiler.dll'))) {
    throw "dxcompiler.dll 必须与 exe 同目录，但 $serverDir 里没有。"
}
Info "服务端 $exe"
$dxilNote = if (Test-Path (Join-Path $serverDir 'dxil.dll')) { '存在' } else { '缺失（可选，不影响）' }
Info "dxil.dll $dxilNote"

# ---------- 4. 写配置 ----------
Step '4/4 写入 Sublime 配置'

$exeJson = $exe.Replace([string][char]92, '/')
if ($ProjectPath) {
    $projJson = $ProjectPath.Replace([string][char]92, '/')
    $cmdArray = '["' + $exeJson + '", "--project", "' + $projJson + '"]'
} else {
    $cmdArray = '["' + $exeJson + '"]'
}

$block = @"
  "microshader": {
    "enabled": true,
    "command": $cmdArray,
    "selector": "source.shaderlab",
    "priority_selector": "source.shaderlab",
    "schemes": ["file", "buffer"],
    "initialization_options": {},
    "settings": {}
  }
"@

$cfgPath = Join-Path $userDir 'LanguageServers.sublime-settings'

if ($DryRun) {
    Info "[DryRun] 目标 $cfgPath"
    foreach ($line in $block.Split([char]10)) { Write-Host "    $line" }
} else {
    New-Item -ItemType Directory -Path $userDir -Force | Out-Null

    if (-not (Test-Path $cfgPath)) {
        Set-Content -Path $cfgPath -Value ("{" + [Environment]::NewLine + $block + "}") -Encoding utf8
        Info "新建 $cfgPath"
    } else {
        $text = Get-Content $cfgPath -Raw
        $range = Get-TopLevelObjectRange -Text $text -Key 'microshader'
        if ($null -ne $range) {
            $new = $text.Substring(0, $range.Start) + $block.TrimStart().TrimEnd() + $text.Substring($range.End + 1)
            Set-Content -Path $cfgPath -Value $new -Encoding utf8
            Info '更新已有的 microshader 段（其它配置与注释未动）'
        } else {
            $last = $text.LastIndexOf('}')
            if ($last -lt 0) { throw "$cfgPath 不是合法对象，无法合并；请手工检查。" }
            $head = $text.Substring(0, $last).TrimEnd()
            $sep = if ($head.EndsWith('{')) { '' } else { ',' }
            $new = $head + $sep + [Environment]::NewLine + $block + $text.Substring($last)
            Set-Content -Path $cfgPath -Value $new -Encoding utf8
            Info '追加 microshader 段（已有内容原样保留）'
        }
    }

    foreach ($name in @('shaderlab.sublime-syntax', 'Default.sublime-keymap')) {
        $src = Join-Path $root "client/sublime/$name"
        if (-not (Test-Path $src)) { Write-Host "  ! 缺少 $src" -ForegroundColor Yellow; continue }
        Copy-Item $src (Join-Path $userDir $name) -Force
        Info "复制 $name"
    }
}

Write-Host ''
Write-Host '下一步：' -ForegroundColor Green
Write-Host '  1. 打开一个 .shader 文件；状态栏左侧应出现 microshader。'
Write-Host '  2. F12 跳定义；Ctrl+Alt+Enter 跟随 #include 超链接。'
Write-Host '  3. Ctrl+Alt+M 打开诊断面板。'
Write-Host '  4. 状态栏没出现服务器名时：Ctrl+Shift+P -> LSP: Troubleshoot Server。'
