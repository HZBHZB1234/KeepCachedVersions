# check_after_run.ps1 —— 实机验收：跑完游戏后检查 IMGUI 剥离问题是否真的修好了
#
# 用法（PowerShell，游戏跑过一次并退出后）：
#   .\check_after_run.ps1
#
# 基线（修复前，2026-09-26 21:54 的 LogOutput.log）：
#   总行数 3818 / 'unstripping' 1257 / 'KeepCachedVersions' 2520
# 备份在 BepInEx\LogOutput.log.before-imguifix.bak

param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Limbus Company"
)

$ErrorActionPreference = "Stop"
$log = Join-Path $GameDir "BepInEx\LogOutput.log"

if (-not (Test-Path $log)) { throw "找不到日志: $log" }

# 用 ReadAllText + -split 而不是 Get-Content：
# Get-Content 会保留 CRLF 里的 \r，导致 `^` 锚定被 \r 破坏（自测时踩到过）。
# 这里按 \r?\n 统一切行，行内不含 \r。
$raw   = [System.IO.File]::ReadAllText($log)
$lines = $raw -split "\r?\n"
$total = $lines.Count

function Count-Of([string]$pattern) {
    return ($lines | Select-String -Pattern $pattern).Count
}

$unstripping = Count-Of 'unstripping'
$kcv         = Count-Of 'KeepCachedVersions'
$notSupp     = Count-Of 'NotSupportedException'
$onGui       = Count-Of 'OnGUI'

# 本插件的错误行有两种形态，必须都算进来：
#   (a) 我们自己的 logger：  [Error  :KeepCachedVersions] ...
#   (b) 别人的 logger 里带着我们的栈：
#       [Error  :Il2CppInterop] Exception ... Method unstripping failed
#          at KeepCachedVersions.CacheCleanupUI.OnGUI()          ← 这一行
#   只按 (a) 过滤会把最关键的 (b) 整片漏掉（自测时踩到过）。
$kcvErrLines = $lines | Where-Object {
    $l = $_
    if ($l -notmatch 'KeepCachedVersions') { return $false }
    if ($l -match '\[Error|\[Warning') { return $true }
    # 栈里直接引用我们代码的那一帧。日志里的缩进是**空格**（实测 3 个），
    # 所以必须写成 `^\s*at`，不能写 `^at`（自测时踩到过）。
    if ($l -match '^\s*at\s+KeepCachedVersions') { return $true }
    return $false
}
$kcvErr = @($kcvErrLines).Count

Write-Host ""
Write-Host "==================== 实机验收 ====================" -ForegroundColor Cyan
Write-Host ("日志: {0}" -f $log)
Write-Host ("生成时间: {0}" -f (Get-Item $log).LastWriteTime)
Write-Host ""
Write-Host "指标                          本次      基线(修复前)" -ForegroundColor Yellow
Write-Host ("总行数                        {0,-9} 3818" -f $total)
Write-Host ("'unstripping' 命中            {0,-9} 1257" -f $unstripping)
Write-Host ("'NotSupportedException' 命中  {0,-9} (含在上一行)" -f $notSupp)
Write-Host ("'KeepCachedVersions' 命中     {0,-9} 2520" -f $kcv)
Write-Host ("'OnGUI' 命中                  {0,-9} 1257" -f $onGui)
Write-Host ""

Write-Host "-------------------- 判定 --------------------" -ForegroundColor Cyan
$pass = $true

if ($unstripping -eq 0) {
    Write-Host "  [PASS] Method unstripping failed 已归零  ← 本次修复的核心指标" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] 仍有 $unstripping 处 unstripping —— 还有 IMGUI（或其它被剥离的 API）在跑" -ForegroundColor Red
    $lines | Select-String -Pattern 'unstripping' -Context 1,3 | Select-Object -First 3 |
        ForEach-Object { Write-Host "         $($_.Line.Trim())" -ForegroundColor DarkGray }
    $pass = $false
}

if ($kcvErr -eq 0) {
    Write-Host "  [PASS] 没有 KeepCachedVersions 的 [Error]/[Warning]/异常栈" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] KeepCachedVersions 有 $kcvErr 条 [Error]/[Warning]/异常栈：" -ForegroundColor Red
    $kcvErrLines | Select-Object -First 8 |
        ForEach-Object { Write-Host "         $($_.Trim())" -ForegroundColor DarkGray }
    $pass = $false
}

Write-Host ""
Write-Host "-------------------- 插件自身日志 --------------------" -ForegroundColor Cyan
# 只取本插件的行；RPGHelper 也有个叫 [UiKit] 的类，别把它的日志混进来
$own = $lines | Where-Object { $_ -match 'KeepCachedVersions' -and $_ -notmatch 'RPGHelper' }
if ($own) {
    $own | Select-Object -First 25 | ForEach-Object { Write-Host "  $($_.Trim())" }
} else {
    Write-Host "  （没有本插件的日志 —— 插件可能没被加载）" -ForegroundColor Yellow
    $pass = $false
}

Write-Host ""
Write-Host "-------------------- 待观察项 --------------------" -ForegroundColor Cyan
$es = $lines | Select-String -Pattern '场景内无 EventSystem'
if ($es) { Write-Host "  · 登录场景原本没有 EventSystem，已由插件补建" -ForegroundColor DarkGray }
else     { Write-Host "  · 登录场景自带 EventSystem（未补建）" -ForegroundColor DarkGray }

$font = $lines | Where-Object { $_ -match 'KeepCachedVersions|\[UiKit\]' -and $_ -match 'TMP 字体' }
if ($font) {
    Write-Host "  · 字体解析结果见上方 TMP 字体 那行（来源 key：custom/fm/scan/sys/tmp）" -ForegroundColor DarkGray
} else {
    Write-Host "  · 没有字体解析日志 —— 可能窗口没打开过（字体是首次画窗口时才解析的）" -ForegroundColor DarkGray
}

Write-Host ""
if ($pass) {
    Write-Host "验收通过 ✓" -ForegroundColor Green
    Write-Host "还需人工确认：模态窗口能弹出、三个按钮可用、长文案不溢出、遮罩能挡住游戏按钮。" -ForegroundColor Yellow
} else {
    Write-Host "验收未通过 —— 请把上面的 [FAIL] 段落贴出来。" -ForegroundColor Red
    exit 1
}
