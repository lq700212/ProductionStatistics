# ======================================================================
# Invoke-UiaLogin.ps1 —— 登录窗 UIA 自动登录助手【V2.4.0】
#
# 用途：软件加了登录窗后，E2E 回归 / 截图 harness 需要无人值守登录。
#       本脚本用 UI Automation 按 AutomationId 找到登录窗里的
#       用户名输入 / 密码输入 / 登录按钮，填值并点登录。
#
# 用法（点源后调用函数，供 Run-E2ETests.ps1 / shot_window.ps1 复用）：
#   . (Join-Path $root "tools\DocShot\Invoke-UiaLogin.ps1")
#   Invoke-UiaLogin -Proc $proc -User admin -Pass 123456
#   Invoke-UiaLogin -Proc $proc -User admin -Pass wrong -NoWaitMain   # 失败场景用例
#
# 返回：$true = 主窗已出现（登录成功）；$false = 未等到主窗/元素缺失。
#       -NoWaitMain 时返回 $true 表示"已成功填值并点击"（不关心登录结果）。
#
# 注意：
#   - 本文件必须 UTF-8 with BOM 保存（含中文，PS5.1 无 BOM 按 ANSI 解码会乱码，铁律 6）。
#   - AutomationId 来自 XAML 的 x:Name（WPF 自动映射）：用户名输入/密码输入/登录按钮。
#   - 登录窗是进程当前的顶层窗口（主窗还没创建），直接用 Process.MainWindowHandle。
# ======================================================================

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 鼠标点击兜底用的 Win32（脚本级只编译一次；函数内重复 Add-Type 同名会抛异常）
if (-not ("UiaMouseClick" -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public class UiaMouseClick {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@
}

function Invoke-UiaLogin {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Proc,
        [string]$User = "admin",
        [string]$Pass = "123456",
        [switch]$NoWaitMain,     # 失败场景：只填值+点击，不等主窗（让调用方断言日志）
        [int]$TimeoutMs = 30000  # 等登录窗 / 等主窗的超时
    )

    # ---------- ① 等登录窗出现（进程第一个窗口） ----------
    # 【坑】Process 对象在窗口出现前访问过 MainWindowHandle 会把 0 缓存死，
    # Refresh() 不会重新解析——必须每次 Get-Process -Id 拿新对象再读。
    $h = [IntPtr]::Zero
    $waited = 0
    while ($waited -lt $TimeoutMs) {
        try {
            $p2 = Get-Process -Id $Proc.Id -ErrorAction SilentlyContinue
            if ($p2 -and $p2.MainWindowHandle -ne 0) { $h = $p2.MainWindowHandle; break }
        } catch { }
        Start-Sleep -Milliseconds 300; $waited += 300
    }
    if ($h -eq [IntPtr]::Zero) { Write-Warning "UIA登录: 超时未等到登录窗"; return $false }
    Write-Host ("  UIA登录: 登录窗 HWND=" + $h)

    # ---------- ② UIA 找三个控件并填值 ----------
    # 【坑】窗口刚出现时 WPF 的 UIA 内容树可能还没建好，且缓存过的 AutomationElement
    # 树不会自动刷新——每轮重试都重新 FromHandle 重建根元素再找。
    function Find-El([IntPtr]$hwnd, [string]$aid) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid)
        for ($i = 0; $i -lt 12; $i++) {
            try {
                $scopeRoot = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $e = $scopeRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
                if ($e -ne $null) { return $e }
            } catch { }
            Start-Sleep -Milliseconds 300
        }
        return $null
    }
    $userEl = Find-El $h "用户名输入"
    $passEl = Find-El $h "密码输入"
    $btnEl  = Find-El $h "登录按钮"
    if ($userEl -eq $null -or $passEl -eq $null -or $btnEl -eq $null) {
        Write-Warning "UIA登录: 登录窗元素缺失（用户名/密码/按钮）"; return $false
    }

    # 填值：ValuePattern 直设；个别环境下 PasswordBox 不支持时回退"聚焦+键盘输入"
    function Set-Value($el, [string]$text, [string]$what) {
        try {
            $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $p.SetValue($text); return $true
        } catch {
            Write-Host ("  UIA登录: " + $what + " ValuePattern 失败，回退键盘输入")
            try {
                $el.SetFocus(); Start-Sleep -Milliseconds 200
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.SendKeys]::SendWait($text); return $true
            } catch { Write-Warning ("UIA登录: " + $what + " 填值失败: " + $_.Exception.Message); return $false }
        }
    }
    if (-not (Set-Value $userEl $User "用户名")) { return $false }
    if (-not (Set-Value $passEl $Pass "密码"))   { return $false }

    # ---------- ③ 点登录（InvokePattern；异常兜底鼠标点按钮中心） ----------
    try {
        $bp = $btnEl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $bp.Invoke()
    } catch {
        $r = $btnEl.Current.BoundingRectangle
        [UiaMouseClick]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2)) | Out-Null
        [UiaMouseClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
        [UiaMouseClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
    Write-Host ("  UIA登录: 已点击登录按钮 (user=" + $User + ")")

    if ($NoWaitMain) { return $true }

    # ---------- ④ 等主窗出现（标题含 生产履历 / Production History，与登录窗标题区分） ----------
    $waited = 0
    while ($waited -lt $TimeoutMs) {
        try {
            $p2 = Get-Process -Id $Proc.Id -ErrorAction SilentlyContinue
            if ($p2 -and ($p2.MainWindowTitle -match "生产履历|Production History")) {
                Write-Host ("  UIA登录: 主窗已出现 TITLE=" + $p2.MainWindowTitle); return $true
            }
        } catch { }
        Start-Sleep -Milliseconds 300; $waited += 300
    }
    Write-Warning "UIA登录: 登录后超时未等到主窗"
    return $false
}
