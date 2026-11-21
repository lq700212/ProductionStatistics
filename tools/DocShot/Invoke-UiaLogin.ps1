# ======================================================================
# Invoke-UiaLogin.ps1 —— 登录窗 UIA 自动登录助手【V2.4.0，V2.4.1 改按需弹窗】
#
# 用途：V2.4.1 起软件启动不再强制登录（直接进主窗看板），登录窗由主窗顶栏
#       "登录"按钮按需弹出。E2E 回归 / 截图 harness 需要无人值守走完整登录链路，
#       本脚本负责：
#         ① 等主窗出现（按窗口标题枚举，不用 MainWindowHandle——主窗就是进程主窗口了）；
#         ② 登录窗没开时，UIA 点主窗顶栏的"登录按钮"把它打开；
#         ③ 按 AutomationId 找登录窗里的 用户名输入/密码输入/登录按钮，填值并点登录；
#         ④ 非 -NoWaitMain 时等登录窗关闭（= 验证通过，DialogResult=true）。
#
# 用法（点源后调用函数，供 Run-E2ETests.ps1 / shot_window.ps1 复用）：
#   . (Join-Path $root "tools\DocShot\Invoke-UiaLogin.ps1")
#   Invoke-UiaLogin -Proc $proc -User admin -Pass 123456
#   Invoke-UiaLogin -Proc $proc -User admin -Pass wrong -NoWaitMain   # 失败场景用例
#
# 返回：$true = 登录窗已关闭（登录成功）；$false = 超时/元素缺失。
#       -NoWaitMain 时返回 $true 表示"已成功填值并点击"（不关心登录结果，登录窗保持打开）。
#
# 注意：
#   - 本文件必须 UTF-8 with BOM 保存（含中文，PS5.1 无 BOM 按 ANSI 解码会乱码，铁律 6）。
#   - AutomationId 来自 XAML 的 x:Name（WPF 自动映射）：主窗"登录按钮"、
#     登录窗"用户名输入"/"密码输入"/"登录按钮"。
#   - 【V2.4.1 坑】不能再用 Process.MainWindowHandle 找登录窗：主窗已是进程主窗口，
#     登录窗是它的模态子窗，MainWindowHandle 只会返回主窗。一律 EnumWindows + 标题匹配
#     （用户登录/Sign in、生产履历/Production History）。
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

# 顶层窗口枚举（按进程 ID 过滤 + 标题匹配）。UiaMouseClick 只管点击，窗口查找单独一个类。
if (-not ("UiaWin" -as [type])) {
    Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class UiaWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static List<IntPtr> TopWindows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) {
    var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString();
  }
}
'@
}

# 在进程的可见顶层窗口里按标题正则找（找到返回句柄，找不到返回 Zero；不等待）
function Find-AppWindow([int]$ProcessId, [string]$Pattern) {
    foreach ($h in [UiaWin]::TopWindows([uint32]$ProcessId)) {
        if ([UiaWin]::Title($h) -match $Pattern) { return $h }
    }
    return [IntPtr]::Zero
}

# 等窗口出现（轮询，超时返回 Zero）
function Wait-AppWindow([int]$ProcessId, [string]$Pattern, [int]$TimeoutMs) {
    $waited = 0
    while ($waited -lt $TimeoutMs) {
        $h = Find-AppWindow $ProcessId $Pattern
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 300; $waited += 300
    }
    return [IntPtr]::Zero
}

# 在指定窗口的 UIA 树里按 AutomationId 找控件（带重试：WPF 内容树刚建好时可能还查不到）
function Find-Aid([IntPtr]$WindowHandle, [string]$AutomationId) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    for ($i = 0; $i -lt 12; $i++) {
        try {
            $scopeRoot = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
            $e = $scopeRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
            if ($e -ne $null) { return $e }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# 点按钮：InvokePattern 优先；个别环境不支持时鼠标点按钮中心兜底
function Invoke-Button($ButtonElement) {
    try {
        $bp = $ButtonElement.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $bp.Invoke()
    } catch {
        $r = $ButtonElement.Current.BoundingRectangle
        [UiaMouseClick]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2)) | Out-Null
        [UiaMouseClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
        [UiaMouseClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
}

# 填值：ValuePattern 直设；个别环境下 PasswordBox 不支持时回退"聚焦+键盘输入"
function Set-Value($Element, [string]$Text, [string]$What) {
    try {
        $p = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $p.SetValue($Text); return $true
    } catch {
        Write-Host ("  UIA登录: " + $What + " ValuePattern 失败，回退键盘输入")
        try {
            $Element.SetFocus(); Start-Sleep -Milliseconds 200
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait($Text); return $true
        } catch { Write-Warning ("UIA登录: " + $What + " 填值失败: " + $_.Exception.Message); return $false }
    }
}

function Invoke-UiaLogin {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Proc,
        [string]$User = "admin",
        [string]$Pass = "123456",
        [switch]$NoWaitMain,     # 失败场景：只填值+点击，不等登录窗关闭（让调用方断言日志）
        [int]$TimeoutMs = 30000  # 等主窗 / 等登录窗 / 等登录窗关闭的超时
    )

    # ---------- ① 等主窗（启动即出现，不再有启动登录窗挡在前面） ----------
    $mainH = Wait-AppWindow $Proc.Id "生产履历|Production History" $TimeoutMs
    if ($mainH -eq [IntPtr]::Zero) { Write-Warning "UIA登录: 超时未等到主窗"; return $false }

    # ---------- ② 登录窗没开 → 点主窗顶栏"登录按钮"打开 ----------
    # 错误密码用例会连续调用两次：第二次登录窗还开着，直接复用，不再点主窗按钮
    # （此时主窗被模态登录窗挡住，点也点不到）。
    $h = Find-AppWindow $Proc.Id "用户登录|Sign in"
    if ($h -eq [IntPtr]::Zero) {
        $openBtn = Find-Aid $mainH "登录按钮"
        if ($openBtn -eq $null) { Write-Warning "UIA登录: 主窗顶栏找不到'登录按钮'"; return $false }
        Invoke-Button $openBtn
        Write-Host "  UIA登录: 已点击主窗'登录按钮'，等待登录窗..."
        $h = Wait-AppWindow $Proc.Id "用户登录|Sign in" $TimeoutMs
        if ($h -eq [IntPtr]::Zero) { Write-Warning "UIA登录: 点击登录按钮后超时未出现登录窗"; return $false }
    }
    Write-Host ("  UIA登录: 登录窗 HWND=" + $h)

    # ---------- ③ UIA 找三个控件并填值 ----------
    # 【坑】窗口刚出现时 WPF 的 UIA 内容树可能还没建好，且缓存过的 AutomationElement
    # 树不会自动刷新——Find-Aid 每轮重试都重新 FromHandle 重建根元素再找。
    $userEl = Find-Aid $h "用户名输入"
    $passEl = Find-Aid $h "密码输入"
    $btnEl  = Find-Aid $h "登录按钮"
    if ($userEl -eq $null -or $passEl -eq $null -or $btnEl -eq $null) {
        Write-Warning "UIA登录: 登录窗元素缺失（用户名/密码/按钮）"; return $false
    }

    if (-not (Set-Value $userEl $User "用户名")) { return $false }
    if (-not (Set-Value $passEl $Pass "密码"))   { return $false }

    Invoke-Button $btnEl
    Write-Host ("  UIA登录: 已点击登录按钮 (user=" + $User + ")")

    if ($NoWaitMain) { return $true }

    # ---------- ④ 等登录窗关闭（验证通过 → DialogResult=true → ShowDialog 返回 → 关窗） ----------
    # 失败时登录窗保持打开并显示红字错误，这里会超时返回 false，调用方断言日志。
    $waited = 0
    while ($waited -lt $TimeoutMs) {
        if ((Find-AppWindow $Proc.Id "用户登录|Sign in") -eq [IntPtr]::Zero) {
            Write-Host "  UIA登录: 登录窗已关闭（登录成功）"; return $true
        }
        Start-Sleep -Milliseconds 300; $waited += 300
    }
    Write-Warning "UIA登录: 登录窗未关闭（账号密码错误或验证未通过）"
    return $false
}
