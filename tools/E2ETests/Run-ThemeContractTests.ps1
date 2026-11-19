# =============================================================================
# Run-ThemeContractTests.ps1 — 主题键契约回归（纯静态检查，不启动程序）
#
# 【干什么】校验"宿主两本主题字典 + Kaleidoscope.UI 库样式"的键契约：
#   ① 宿主 Themes/Light.xaml 与 Themes/Dark.xaml 的键集必须完全一致
#      （同一键只在一本里有 → 切主题时该处颜色不跟着变，属于静默失效）；
#   ② Kaleidoscope.UI 的 ShellStyles.xaml 里所有 {DynamicResource 键} 必须
#      在两本宿主主题里都存在（库样式在运行时用元素上下文解析 DynamicResource，
#      宿主主题字典是唯一的值来源；缺键 → 控件透明/文字黑色，不报错）；
#   ③ 宿主主题字典里禁止 <StaticResource ResourceKey="..."/> 元素转发
#      （net472 BAML 键值错位会炸主窗，历史血泪，见 kaleidoscope AGENTS.md）。
#
# 【为什么要有】V2.3.0 把主窗 UI 真接入库控件后，主题键面板翻倍；
#   手改键板最容易犯"只补一本""补了库键忘了宿主"两类错，且这两类错
#   编译期完全静默。本脚本把检查固化成可重复执行的回归。
#
# 【怎么跑】仓库根目录执行：
#   powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-ThemeContractTests.ps1
#   全部通过退出码 0；任一失败退出码 1（可接 CI）。
#
# 【注意】脚本文件必须 UTF-8 带 BOM（PowerShell 5.1 解析中文注释/输出的前提）。
# =============================================================================

param(
    [string]$宿主目录 = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'ProductionStatistics')
)

$ErrorActionPreference = 'Stop'
$失败数 = 0
$通过数 = 0

function 断言 {
    param([bool]$条件, [string]$说明, [string]$细节 = '')
    if ($条件) {
        $script:通过数++
        Write-Host ("  [PASS] " + $说明) -ForegroundColor Green
    } else {
        $script:失败数++
        Write-Host ("  [FAIL] " + $说明) -ForegroundColor Red
        if ($细节) { Write-Host ("         " + $细节) -ForegroundColor DarkYellow }
    }
}

# 从 XAML 里提取所有 x:Key="..."（Color/SolidColorBrush/CornerRadius/sys:Double/
# LinearGradientBrush/DropShadowEffect/ResourceDictionary 里的任意元素都覆盖）
function 取键集 {
    param([string]$路径)
    $文本 = [IO.File]::ReadAllText($路径, (New-Object Text.UTF8Encoding($false, $true)))
    $集合 = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($m in [regex]::Matches($文本, 'x:Key\s*=\s*"([^"]+)"')) {
        [void]$集合.Add($m.Groups[1].Value)
    }
    return $集合
}

# 从 XAML 里提取所有 {DynamicResource 键} / {DynamicResource ResourceKey=键}
function 取动态资源键集 {
    param([string]$路径)
    $文本 = [IO.File]::ReadAllText($路径, (New-Object Text.UTF8Encoding($false, $true)))
    $集合 = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($m in [regex]::Matches($文本, '\{DynamicResource\s+(?:ResourceKey\s*=\s*)?([^\}\s,]+)\s*\}')) {
        [void]$集合.Add($m.Groups[1].Value)
    }
    return $集合
}

$浅色 = Join-Path $宿主目录 'Themes\Light.xaml'
$深色 = Join-Path $宿主目录 'Themes\Dark.xaml'
$库样式 = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'extern\kaleidoscope\Kaleidoscope.UI\Themes\ShellStyles.xaml'

Write-Host ''
Write-Host '=== 主题键契约回归（Run-ThemeContractTests） ===' -ForegroundColor Cyan
Write-Host ("宿主目录: " + $宿主目录)

断言 (Test-Path $浅色) '浅色主题字典存在' $浅色
断言 (Test-Path $深色) '深色主题字典存在' $深色
断言 (Test-Path $库样式) 'Kaleidoscope.UI ShellStyles.xaml 存在' $库样式
if ($失败数 -gt 0) { exit 1 }

$浅键 = 取键集 $浅色
$深键 = 取键集 $深色

Write-Host ''
Write-Host ("[1] 键集一致性：Light=" + $浅键.Count + " 键，Dark=" + $深键.Count + " 键")
$仅浅 = @($浅键 | Where-Object { -not $深键.Contains($_) } | Sort-Object)
$仅深 = @($深键 | Where-Object { -not $浅键.Contains($_) } | Sort-Object)
断言 ($仅浅.Count -eq 0) 'Light 的键都在 Dark 里' ("仅 Light 有: " + ($仅浅 -join ', '))
断言 ($仅深.Count -eq 0) 'Dark 的键都在 Light 里' ("仅 Dark 有: " + ($仅深 -join ', '))

Write-Host ''
Write-Host '[2] 库样式 DynamicResource 键 ⊆ 宿主键板'
$库键 = 取动态资源键集 $库样式
$缺浅 = @($库键 | Where-Object { -not $浅键.Contains($_) } | Sort-Object)
$缺深 = @($库键 | Where-Object { -not $深键.Contains($_) } | Sort-Object)
断言 ($缺浅.Count -eq 0) ("库样式引用的 " + $库键.Count + " 个键在 Light 里都有") ("缺: " + ($缺浅 -join ', '))
断言 ($缺深.Count -eq 0) ("库样式引用的 " + $库键.Count + " 个键在 Dark 里都有") ("缺: " + ($缺深 -join ', '))

Write-Host ''
Write-Host '[3] 宿主主题禁止 StaticResource 元素转发'
$转发 = 0
foreach ($文件 in @($浅色, $深色)) {
    $文本 = [IO.File]::ReadAllText($文件, (New-Object Text.UTF8Encoding($false, $true)))
    $命中 = [regex]::Matches($文本, '<StaticResource\s+ResourceKey\s*=')
    $转发 += $命中.Count
    断言 ($命中.Count -eq 0) ((Split-Path -Leaf $文件) + ' 无 <StaticResource ResourceKey=.../> 元素') ("命中 " + $命中.Count + " 处")
}

Write-Host ''
Write-Host '=== 汇总 ===' -ForegroundColor Cyan
Write-Host ("PASS=" + $通过数 + "  FAIL=" + $失败数)
if ($失败数 -gt 0) {
    Write-Host '结果: 失败（键契约不满足，先修键板再提交）' -ForegroundColor Red
    exit 1
}
Write-Host '结果: 全部通过' -ForegroundColor Green
exit 0
