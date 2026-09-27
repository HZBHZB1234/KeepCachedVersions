# KeepCachedVersions —— 编译 / 部署
#
# 用法（PowerShell）：
#   .\build.ps1               # 编译
#   .\build.ps1 -Deploy       # 编译 + 部署到游戏的 BepInEx\plugins
#   .\build.ps1 -RefDir <路径> # 换用另一套 interop 引用（默认 Lethe 分发包）
#
# 说明：本工程没有任何 NuGet 依赖（只 <Reference> 游戏自带的 interop 程序集），
# 所以用 --no-restore 构建，绕开本机 dotnet restore 的环境问题。
#
# v0.3.0 起本插件只做"阻止删除"，无 UI；原先的 verify_ugui.py 前置校验已随 UI 一起移除。
# 产物仍会扫一遍 IMGUI 关键字，防止将来有人把 IMGUI 代码带回来（本作 IMGUI 被剥离，
# 调用必抛 NotSupportedException: Method unstripping failed）。

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

# ---------------------------------------------------------------- 1) 编译
Write-Host "[1/2] 编译 ..." -ForegroundColor Cyan
$buildArgs = @($proj, "-c", "Release", "--no-restore", "--nologo")
if ($RefDir) { $buildArgs += "-p:RefDir=$RefDir" }
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
if (-not (Test-Path $out)) { throw "找不到产物: $out" }
Write-Host "      产物: $out" -ForegroundColor Green

# ------------------------------------------------- 产物里不许残留 IMGUI 引用
Write-Host "      检查产物是否残留 IMGUI 引用 ..." -ForegroundColor Cyan
$bytes = [System.IO.File]::ReadAllBytes($out)
$text  = [System.Text.Encoding]::ASCII.GetString($bytes)
$bad = @()
foreach ($n in @("IMGUIModule", "GUILayout", "GUIStyle", "OnGUI", "GUIContent", "GUISkin")) {
    if ($text.Contains($n)) { $bad += $n }
}
if ($bad.Count -gt 0) { throw "产物里仍残留 IMGUI 引用: $($bad -join ', ')" }
Write-Host "      干净 ✓" -ForegroundColor Green

# ---------------------------------------------------------------- 2) 部署
if ($Deploy) {
    if (-not (Test-Path $GameDir)) { throw "游戏目录不存在: $GameDir" }
    $plugins = Join-Path $GameDir "BepInEx\plugins"
    if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

    Write-Host "[2/2] 部署 ..." -ForegroundColor Cyan
    $running = Get-Process -Name "LimbusCompany" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "      ⚠️ 游戏正在运行，DLL 被锁定，覆盖会失败。请先关闭游戏。" -ForegroundColor Yellow
        throw "游戏正在运行"
    }
    Copy-Item $out $plugins -Force
    Write-Host "      -> $plugins\KeepCachedVersions.dll" -ForegroundColor Green
    Write-Host ""
    Write-Host "完成。启动游戏后验收看 BepInEx\LogOutput.log 应出现：" -ForegroundColor Yellow
    Write-Host "  [KeepCachedVersions] no-delete patch active: ClearCachedVersions is a no-op"
    Write-Host "  [KeepCachedVersions] old-cache patch active: AddressableManager.ClearOldCache is a no-op"
    Write-Host "  [KeepCachedVersions] fallback patch active: ClearCachedVersionInternal is a no-op"
    Write-Host "且不应再有 login scene / UI / unstripping 相关行。"
}
}
finally { Pop-Location }
