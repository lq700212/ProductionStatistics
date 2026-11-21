# shot_window.ps1 —— 真 exe 窗口截图取证小工具（DocShot 全量流水线的轻量替代）
# 用途：UI 细节改动（圆角/边距/配色）后的快速视觉验证——启动真程序、按窗口状态截图、
#       自动裁出四角放大图，人工/AI 直接看图对比，不用跑完整 DocShot 十场景流水线。
# 原理：PrintWindow(PW_RENDERFULLCONTENT) 对 WPF（DX 渲染）可靠取帧；
#       DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9) 取真实边界（剔除隐形边框）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\DocShot\shot_window.ps1 `
#     -Exe "ProductionStatistics\bin\Debug\net472\ProductionStatistics.Wpf.exe" `
#     -OutDir "C:\Temp\shots" -States Restored,Maximized -Crops 260 -Kill
# 注意：本文件是 UTF-8 带 BOM（含中文，无 BOM 禁止 -File 直跑，见 AGENTS.md 铁律 6）。
param(
    [Parameter(Mandatory = $true)][string]$Exe,        # 待截图的 exe 路径
    [Parameter(Mandatory = $true)][string]$OutDir,     # 截图输出目录（自动创建）
    [string]$LoginUser = 'admin',                      # 【V2.4.0】登录窗自动登录用户名（空=不登录，截登录窗）
    [string]$LoginPass = '123456',                     # 【V2.4.0】登录窗自动登录密码
    [string]$States = 'Restored',                      # 状态列表，逗号分隔：Restored,Maximized
                                                       # （powershell -File 对数组参数支持差，统收单字符串再拆）
    [int]$Crops = 260,                                 # 角部裁剪边长（物理像素）
    [double]$Zoom = 2.6,                               # 角部放大倍数
    [int]$SettleSeconds = 6,                           # 启动后等待界面稳定的秒数
    [switch]$Kill                                      # 截完结束进程（避免残留实例锁 exe）
)
$stateList = @($States -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Win32：DPI 感知（否则 GetWindowRect 是逻辑坐标，截图会错位）+ 窗口操作三件套
Add-Type '
using System;
using System.Runtime.InteropServices;
public class ShotWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}'
[ShotWin]::SetProcessDPIAware() | Out-Null

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exeFull = (Resolve-Path $Exe).Path
$proc = Start-Process -FilePath $exeFull -WorkingDirectory (Split-Path $exeFull) -PassThru

# 【V2.4.1】启动直接进主窗（不再强制登录）：
#   - 给了 LoginUser：点主窗顶栏"登录按钮"→ 自动登录 → 等登录窗关闭 → 截主窗；
#   - LoginUser 为空：点开登录窗但不登录，截登录窗本身（演示/文档用）。
. (Join-Path $PSScriptRoot 'Invoke-UiaLogin.ps1')

$h = Wait-AppWindow $proc.Id "生产履历|Production History" 30000
if ($h -eq [IntPtr]::Zero) { throw "30 秒内未等到主窗：$exeFull" }

if ($LoginUser -ne '') {
    $ok = Invoke-UiaLogin -Proc $proc -User $LoginUser -Pass $LoginPass -TimeoutMs 30000
    if (-not $ok) { throw "自动登录失败：$exeFull" }
    $h = Wait-AppWindow $proc.Id "生产履历|Production History" 5000
} else {
    # 只打开登录窗（不登录），供截登录窗界面
    $openBtn = Find-Aid $h "登录按钮"
    if ($openBtn -ne $null) {
        Invoke-Button $openBtn
        $loginH = Wait-AppWindow $proc.Id "用户登录|Sign in" 15000
        if ($loginH -ne [IntPtr]::Zero) { $h = $loginH }
    }
}
Start-Sleep -Seconds $SettleSeconds

# 拍一张：PrintWindow 到位图；返回位图。失败（全黑）时回退屏幕拷贝。
function Shot-Window([IntPtr]$handle, [int]$wd, [int]$ht) {
    $bmp = New-Object System.Drawing.Bitmap($wd, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [ShotWin]::PrintWindow($handle, $dc, 2) | Out-Null   # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($dc); $g.Dispose()
    return $bmp
}

# 裁四角放大图：命名 <前缀>_<tl|tr|bl|br>.png
function Crop-Corners([System.Drawing.Bitmap]$bmp, [string]$prefix) {
    $wd = $bmp.Width; $ht = $bmp.Height; $c = [int]$Crops
    $spots = @{
        'tl' = @(0, 0); 'tr' = @([Math]::Max(0, $wd - $c), 0)
        'bl' = @(0, [Math]::Max(0, $ht - $c)); 'br' = @([Math]::Max(0, $wd - $c), [Math]::Max(0, $ht - $c))
    }
    foreach ($k in $spots.Keys) {
        $x = [int]$spots[$k][0]; $y = [int]$spots[$k][1]
        $rc = New-Object System.Drawing.Rectangle($x, $y, [Math]::Min($c, $wd), [Math]::Min($c, $ht))
        $part = $bmp.Clone($rc, $bmp.PixelFormat)
        $big = New-Object System.Drawing.Bitmap($part, [int]($rc.Width * $Zoom), [int]($rc.Height * $Zoom))
        $big.Save((Join-Path $OutDir ($prefix + '_' + $k + '.png')))
        $big.Dispose(); $part.Dispose()
    }
}

foreach ($state in $stateList) {
    if ($state -eq 'Maximized') {
        [ShotWin]::ShowWindow($h, 3) | Out-Null           # SW_MAXIMIZE
        Start-Sleep -Seconds 2
    }
    [ShotWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 600

    $r = New-Object ShotWin+RECT
    if ([ShotWin]::DwmGetWindowAttribute($h, 9, [ref]$r, [Runtime.InteropServices.Marshal]::SizeOf($r)) -ne 0) {
        [ShotWin]::GetWindowRect($h, [ref]$r) | Out-Null
    }
    $wd = $r.R - $r.L; $ht = $r.B - $r.T
    Write-Host ("[{0}] window {1}x{2} @({3},{4})" -f $state, $wd, $ht, $r.L, $r.T)

    $bmp = Shot-Window $h $wd $ht
    $bmp.Save((Join-Path $OutDir ($state.ToLower() + '_full.png')))
    Crop-Corners $bmp ($state.ToLower())
    $bmp.Dispose()
}

if ($Kill) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Host 'process killed'
}
Write-Host ('done -> ' + $OutDir)
