# ======================================================================
# 生产统计 · 端到端自动化回归（一键全量）
#
# 测什么（对应 V2.2.2 计数可靠性改造的验收口径，全部自动化断言）：
#   T1 构建通过        —— dotnet build 0 error
#   T2 授权注册        —— 日志出现 "HSL 授权状态: 已激活"，全程无 "系统授权失败"
#   T3 合并读生效      —— "合并读=启用" + 时间账目里 合并读=1（单轮周期应 <160ms）
#   T4 计数与脉冲源一致 —— FakePlc normal 模式 1.8s/脉冲（≈33 件/分钟），
#                          本分钟产量 合计 ∈ [20, 40]，无漏计无爆发
#   T5 时间账目守恒    —— 每分钟 "差额=X.X秒" < 1.0（四类时间之和≈墙上时间）
#   T6 重启无爆发      —— 杀掉软件重启（FakePlc 不动、触发位残留 ON，复现现场
#                          11-19 "398 件" 事故场景），重启后分钟产量仍 ∈ [20, 40]
#   T7 无 ERROR        —— 全程日志零 ERROR 行
#
# 用法（项目根执行）：
#   powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1
#   # 可选参数：
#   #   -SkipBuild      跳过 dotnet build（已构建过时加速）
#   #   -KeepOutput     保留测试运行目录与日志（排障用，默认跑完清理）
#   #   -HslDat <路径>  hsl.dat 授权文件路径（默认 E:\Project\HslCommunicationLicense\hsl.dat；
#   #                   找不到时 T2 降级为"跳过授权断言"并 WARN，其余用例照跑）
#
# 实现要点（改脚本前必读）：
#   - 本脚本必须 UTF-8 with BOM 保存（PS5.1 对无 BOM 的 UTF-8 按 ANSI 解码，
#     中文字符串会静默乱码——项目 AGENTS.md 铁律 6）。
#   - 兼容 Windows PowerShell 5.1：不用 PS7 三元运算符；函数调用参数用空格分隔
#     而非逗号（"Assert (x), 'a', 'b'" 的逗号会让三个实参被打包成一个数组——PowerShell 经典坑）。
#   - FakePlc 的模式文件由脚本显式传入（args[1]），并预先写好 "normal"，
#     不依赖 %TEMP% 里的残留文件，保证脉冲源确定性（1.8s/脉冲 ≈33 件/分钟）。
#   - 断言全部从运行日志文本提取（时间账目/本分钟产量/小时结算），不碰界面。
# ======================================================================

param(
    [switch]$SkipBuild,
    [switch]$KeepOutput,
    [string]$HslDat = "E:\Project\HslCommunicationLicense\hsl.dat"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # 项目根（tools\E2ETests 的上两级）
$run  = Join-Path $PSScriptRoot "run"

# ---------------- 结果登记 ----------------
$script:passList = New-Object System.Collections.Generic.List[string]
$script:failList = New-Object System.Collections.Generic.List[string]

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) {
        $script:passList.Add($name) | Out-Null
        Write-Output ("  [PASS] " + $name + " :: " + $detail)
    } else {
        $script:failList.Add($name) | Out-Null
        Write-Output ("  [FAIL] " + $name + " :: " + $detail)
    }
}

# ---------------- 准备 ----------------
Write-Output "===== 生产统计端到端回归 ====="
if (Test-Path $run) { Remove-Item -Recurse -Force $run }
New-Item -ItemType Directory -Force $run | Out-Null

# T1 构建
if (-not $SkipBuild) {
    Write-Output ""
    Write-Output "[T1] 构建主工程..."
    $buildOut = & dotnet build (Join-Path $root "ProductionStatistics\ProductionStatistics.csproj") -c Debug -v:q --nologo 2>&1
    $buildCode = $LASTEXITCODE
    $buildText = ($buildOut | Out-String)
    if ($buildCode -eq 0) {
        Assert $true "T1 构建通过" "0 error"
    } else {
        Assert $false "T1 构建通过" "dotnet build 非 0 退出"
        Write-Output $buildText
        throw "构建失败，终止回归"
    }
}

$binDir = Join-Path $root "ProductionStatistics\bin\Debug\net472"
if (-not (Test-Path (Join-Path $binDir "ProductionStatistics.Wpf.exe"))) {
    throw "找不到构建产物 " + $binDir + " ，请先构建"
}

# 部署测试运行目录（exe + 全部 DLL + 授权 + 测试 ini）
Copy-Item (Join-Path $binDir "*") $run -Force -Recurse
$hasLicense = Test-Path $HslDat
if ($hasLicense) {
    Copy-Item $HslDat (Join-Path $run "hsl.dat") -Force
    Write-Output ("  已部署授权文件: " + $HslDat)
} else {
    Write-Output ("  [WARN] 找不到 hsl.dat（" + $HslDat + "），T2 授权断言降级为跳过")
}

# 测试 ini：IP 指向本机 FakePlc；现场地址段原样（M3200~M3232 连续，触发合并读路径）
$iniPath = Join-Path $run "生产统计.ini"
$iniText = @(
    '# 生产统计 配置文件（E2E 测试用，UTF-8）',
    '[PLC]',
    'IP=127.0.0.1',
    'Port=6060',
    '轮询间隔Ms=100',
    '信号确认OFF毫秒=300',
    '计数最小间隔Ms=1500',
    '',
    '[界面]',
    '设备名称=点胶固化收料机',
    '设备名称En=Dispensing-Curing-Collecting Machine',
    '语言=中文',
    '主题=浅色',
    '',
    '[PLC地址]',
    '设备自动中=M3200',
    '设备报警=M3201',
    '焊接OK=M3210',
    '焊接NG=M3211',
    '焊接IDLE=M3212',
    '气密OK=M3220',
    '气密NG=M3221',
    '气密IDLE=M3222',
    '贴标OK=M3230',
    '贴标NG=M3231',
    '贴标IDLE=M3232',
    '不复位地址='
) -join "`r`n"
[System.IO.File]::WriteAllText($iniPath, $iniText, (New-Object System.Text.UTF8Encoding($true)))

# 编译 FakePlc（/codepage:65001 防中文乱码，见 FakePlc 源文件头注释）
Write-Output ""
Write-Output "[准备] 编译 FakePlc（假 PLC）..."
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fakeSrc = Join-Path $root "tools\DocShot\FakePlc.cs"
$fakeExe = Join-Path $run "FakePlc.exe"
& $csc /nologo /codepage:65001 /target:exe /out:$fakeExe `
    /reference:"$root\HslCommunication.dll" $fakeSrc | Out-Null
if ($LASTEXITCODE -ne 0) { throw "FakePlc 编译失败" }

# 模式文件：normal = 每 1.8s 一脉冲轮转三单元，每 12 件夹 1 NG（≈33 件/分钟，确定性脉冲源）
$modeFile = Join-Path $run "fakeplc_mode.txt"
[System.IO.File]::WriteAllText($modeFile, "normal", (New-Object System.Text.UTF8Encoding($false)))

# ---------------- 用例执行 ----------------
function Start-Stack {
    Start-Process $fakeExe -ArgumentList @("6060", $modeFile, (Join-Path $run "fakeplc.log")) -WorkingDirectory $run -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Start-Process (Join-Path $run "ProductionStatistics.Wpf.exe") -WorkingDirectory $run
    Start-Sleep -Seconds 75   # 覆盖启动 + 第一个完整"时间账目"分钟窗口
}

function Stop-AppOnly {
    Get-Process "ProductionStatistics.Wpf" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function Stop-Stack {
    Stop-AppOnly
    Get-Process "FakePlc" -ErrorAction SilentlyContinue | Stop-Process -Force
}

$today = Get-Date -Format "yyyy-MM-dd"
$logPath = Join-Path $run ("logs\运行日志_" + $today + ".log")

try {
    Write-Output ""
    Write-Output "[阶段一] 首次启动（T2 授权 / T3 合并读 / T4 计数 / T5 时间账目）..."
    Start-Stack

    $log = [System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8)
    $logText = ($log -join "`n")

    # T2 授权
    if ($hasLicense) {
        Assert ($logText -match "HSL 授权状态: 已激活") "T2 授权注册成功" "日志含'已激活'"
        Assert (-not ($logText -match "系统授权失败")) "T2 全程无授权失败" "零'系统授权失败'"
    } else {
        Write-Output "  [SKIP] T2 授权断言（无 hsl.dat）"
    }

    # T3 合并读
    Assert ($logText -match "合并读=启用") "T3 合并读启用（启动日志）" "1 次报文读回 11 位"
    $账目行 = @($log | Where-Object { $_ -match "时间账目.*合并读=1" })
    Assert ($账目行.Count -ge 1) "T3 合并读生效（时间账目）" ("合并读=1 出现 " + $账目行.Count + " 次")
    if ($账目行.Count -ge 1) {
        if ($账目行[0] -match "单轮周期avg=(\d+)ms") {
            $avgMs = [int]$Matches[1]
            Assert ($avgMs -lt 160) "T3 单轮周期 <160ms" ("实测 avg=" + $avgMs + "ms（4 次报文时代 200~330ms）")
        }
    }

    # T4 计数与脉冲源一致 + T5 时间账目守恒
    $产量行 = @($log | Where-Object { $_ -match "本分钟产量" })
    foreach ($row in $产量行) {
        if ($row -match "合计=(\d+)") {
            $合计 = [int]$Matches[1]
            Assert (($合计 -ge 20) -and ($合计 -le 40)) "T4 分钟产量与脉冲源一致" ("合计=" + $合计 + "（脉冲源≈33 件/分钟）")
        }
    }
    $差额行 = @($log | Where-Object { $_ -match "时间账目" })
    foreach ($row in $差额行) {
        if ($row -match "差额=([\d\.]+)秒") {
            $diff = [double]$Matches[1]
            Assert ($diff -lt 1.0) "T5 时间账目守恒" ("差额=" + $diff + "秒 <1.0")
        }
    }

    # T6 重启无爆发（残留 ON 场景：FakePlc 继续跑，只杀软件再启）
    Write-Output ""
    Write-Output "[阶段二] 重启软件（复现现场'残留 ON 重启'事故场景）..."
    Stop-AppOnly
    Start-Process (Join-Path $run "ProductionStatistics.Wpf.exe") -WorkingDirectory $run
    Start-Sleep -Seconds 75

    $log2 = [System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8)
    $首次采样次数 = @($log2 | Where-Object { $_ -match "首次采样完成" }).Count
    Assert ($首次采样次数 -ge 2) "T6 重启完成（第二次首次采样）" ("首次采样出现 " + $首次采样次数 + " 次")

    # 重启后的分钟产量：取最后一次"首次采样完成"之后的行，全部必须在脉冲源带宽内（不爆发）
    $startIdx = 0
    for ($i = $log2.Count - 1; $i -ge 0; $i--) { if ($log2[$i] -match "首次采样完成") { $startIdx = $i; break } }
    if ($startIdx -lt $log2.Count - 1) {
        $重启后产量 = @($log2[($startIdx + 1)..($log2.Count - 1)] | Where-Object { $_ -match "本分钟产量" })
    } else {
        $重启后产量 = @()
    }
    Assert ($重启后产量.Count -ge 1) "T6 重启后有产量数据" ("重启后分钟产量行 " + $重启后产量.Count + " 条")
    foreach ($row in $重启后产量) {
        if ($row -match "合计=(\d+)") {
            $合计 = [int]$Matches[1]
            Assert (($合计 -ge 20) -and ($合计 -le 40)) "T6 重启后无重复计数爆发" ("合计=" + $合计 + "（现场事故场景老代码会飙到 60+）")
        }
    }

    # T7 全程零 ERROR
    $errLines = @($log2 | Where-Object { $_ -match "\[ERROR" })
    Assert ($errLines.Count -eq 0) "T7 全程零 ERROR" ("ERROR 行 " + $errLines.Count + " 条")
    if ($errLines.Count -gt 0) {
        $errLines | Select-Object -First 5 | ForEach-Object { Write-Output ("    " + $_) }
    }
}
finally {
    Stop-Stack
    if (-not $KeepOutput) {
        Start-Sleep -Seconds 1
        Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
        Write-Output ""
        Write-Output "（测试运行目录已清理；加 -KeepOutput 可保留排障）"
    } else {
        Write-Output ""
        Write-Output ("（测试产物保留在 " + $run + "）")
    }
}

# ---------------- 汇总 ----------------
Write-Output ""
Write-Output "===== 回归汇总 ====="
Write-Output ("PASS = " + $script:passList.Count + "  FAIL = " + $script:failList.Count)
if ($script:failList.Count -gt 0) {
    Write-Output "失败用例："
    $script:failList | ForEach-Object { Write-Output ("  - " + $_) }
    exit 1
}
Write-Output "全部通过。"
exit 0
