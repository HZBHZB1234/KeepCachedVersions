# KeepCachedVersions —— 校验 / 构建 / 部署
#
# 用法（PowerShell）：
#   .\build.ps1               # 校验 + 编译
#   .\build.ps1 -Deploy       # 校验 + 编译 + 部署到游戏的 BepInEx\plugins
#
# 说明：本工程没有任何 NuGet 依赖（只 <Reference> 游戏自带的 interop 程序集），
# 所以用 --no-restore 构建，绕开本机 dotnet restore 的环境问题。

param(
    [switch]$Deploy,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
    [string]$RefDir  = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here "KeepCachedVersions.csproj"
$out  = Join-Path $here "bin\Release\net6.0\KeepCachedVersions.dll"

Push-Location $here
try {

# ---------------------------------------------------------------- 0) 找 python
# 某些环境（IDE / 服务 / 精简 PATH）里 `python` 不在 PATH 上，脚本会**静默**卡在第一步，
# 所以显式解析一次，找不到就给明确报错，而不是留下一行没头没尾的输出。
$py = $null
foreach ($name in @("python", "python3")) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { $py = $cmd.Source; break }
}
if (-not $py) {
    foreach ($fallback in @(
        "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
        "$env:USERPROFILE\.workbuddy-ai\binaries\python\versions\3.13.12\python.exe"
    )) {
        if (Test-Path $fallback) { $py = $fallback; break }
    }
}
if (-not $py) { throw "找不到 python 解释器，无法运行 verify_ugui.py" }
Write-Host "      python = $py" -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1) 校验
Write-Host "[1/3] 校验 uGUI/TMP 依赖 + 反查 IMGUI 残留 ..." -ForegroundColor Cyan
& $py -X utf8 (Join-Path $here "verify_ugui.py")
if ($LASTEXITCODE -ne 0) {
    Write-Host "      校验未通过 —— 用到的绘制 API 可能被 IL2CPP 剥离了，或源码里又混进了 IMGUI。" -ForegroundColor Red
    Write-Host "      IMGUI（OnGUI）在本作**整体不可用**：原生实现被剥掉，调用必抛" -ForegroundColor Red
    Write-Host "      System.NotSupportedException: Method unstripping failed。" -ForegroundColor Red
    throw "uGUI/IMGUI 校验失败"
}
Write-Host "      全部可用" -ForegroundColor Green

# ---------------------------------------------------------------- 2) 编译
Write-Host "[2/3] 编译 ..." -ForegroundColor Cyan
$buildArgs = @($proj, "-c", "Release", "--no-restore", "--nologo")
if ($RefDir) { $buildArgs += "-p:RefDir=$RefDir" }
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
if (-not (Test-Path $out)) { throw "找不到产物: $out" }
Write-Host "      产物: $out" -ForegroundColor Green

# ------------------------------------------------- 2b) 产物里不许残留 IMGUI 引用
Write-Host "      检查产物是否残留 IMGUI 引用 ..." -ForegroundColor Cyan
$bytes = [System.IO.File]::ReadAllBytes($out)
$text  = [System.Text.Encoding]::ASCII.GetString($bytes)
$bad = @()
foreach ($n in @("IMGUIModule", "GUILayout", "GUIStyle", "OnGUI", "GUIContent", "GUISkin")) {
    if ($text.Contains($n)) { $bad += $n }
}
if ($bad.Count -gt 0) { throw "产物里仍残留 IMGUI 引用: $($bad -join ', ')" }
Write-Host "      干净 ✓" -ForegroundColor Green

# ---------------------------------------------------------------- 3) 部署
if ($Deploy) {
    if (-not (Test-Path $GameDir)) { throw "游戏目录不存在: $GameDir" }
    $plugins = Join-Path $GameDir "BepInEx\plugins"
    if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

    Write-Host "[3/3] 部署 ..." -ForegroundColor Cyan
    $running = Get-Process -Name "LimbusCompany" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "      ⚠️ 游戏正在运行，DLL 被锁定，覆盖会失败。请先关闭游戏。" -ForegroundColor Yellow
        throw "游戏正在运行"
    }
    Copy-Item $out $plugins -Force
    Write-Host "      -> $plugins\KeepCachedVersions.dll" -ForegroundColor Green
    Write-Host ""
    Write-Host "完成。启动游戏进开始页后：" -ForegroundColor Yellow
    Write-Host "  1) 按钮文案应为当前语言的「清除缓存」"
    Write-Host "  2) 点击应弹出模态窗口（不再空白 / 不再刷 NotSupportedException）"
    Write-Host "  3) 验收看 BepInEx\LogOutput.log：Method unstripping failed 应归零"
}
}
finally { Pop-Location }
