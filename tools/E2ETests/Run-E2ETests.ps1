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
#   T8 数据库落库      —— 【V2.3.1】主存储=SQLite：数据\生产统计.db 已建、当天小时行落库、
#                          脉冲源产量计数进了库（三单元 OK 合计 >0）
#   T9 登录·错误密码拒绝 —— 【V2.4.0】UIA 输 admin+错误密码：日志有"登录失败"、主窗不出现
#   T10 登录·正确通过    —— 【V2.4.0】UIA 输 admin/123456：日志出现"主窗体初始化完成"
#   T11 启动自动选页签   —— 【V2.4.0】日志"启动自动选页签: 序号=X"与当前时刻应选的班次一致
#                          （8~19 点→0 当天白班；≥20 点→1 当天夜班；<8 点→3 昨天夜班）
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
#   - 【V2.4.1】exe 启动不再强制登录（直接进主窗看板）；所有阶段用
#     tools\DocShot\Invoke-UiaLogin.ps1 走"点顶栏登录按钮 → 登录窗 → 自动登录"链路
#     （admin/123456，与 LoginService 预置账号一致）；登录助手点源自 DocShot 目录。
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
# 【V2.3.1】bin 目录的 logs/ 与 数据/ 会被上面的通配拷贝一并带进来——历史日志污染 T2/T7
# 断言（旧"未授权"提示行含"系统授权失败"字样），残留 生产统计.db 会让 T8 读到旧数据且
# 跳过"空库导入"链路。测试必须从全新数据状态起跑，删掉这两处：
Remove-Item (Join-Path $run "logs") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $run "数据") -Recurse -Force -ErrorAction SilentlyContinue
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
    /reference:"$root\libs\HslCommunication.dll" $fakeSrc | Out-Null
if ($LASTEXITCODE -ne 0) { throw "FakePlc 编译失败" }

# 模式文件：normal = 每 1.8s 一脉冲轮转三单元，每 12 件夹 1 NG（≈33 件/分钟，确定性脉冲源）
$modeFile = Join-Path $run "fakeplc_mode.txt"
[System.IO.File]::WriteAllText($modeFile, "normal", (New-Object System.Text.UTF8Encoding($false)))

# 登录助手点源（【V2.4.1】启动直接进主窗；助手会点主窗顶栏"登录按钮"打开登录窗再登录）
. (Join-Path $root "tools\DocShot\Invoke-UiaLogin.ps1")

# ---------------- 用例执行 ----------------
#【V2.4.0】FakePlc 全程只起一次（阶段零起，阶段一/二复用，避免重复占用 6060 端口）；
# Start-Stack 只管"起 app + 自动登录 + 等窗口期"。
function Start-FakePlcOnce {
    if (Get-Process "FakePlc" -ErrorAction SilentlyContinue) { return }
    Start-Process $fakeExe -ArgumentList @("6060", $modeFile, (Join-Path $run "fakeplc.log")) -WorkingDirectory $run -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

function Start-AppWithLogin {
    Start-Process (Join-Path $run "ProductionStatistics.Wpf.exe") -WorkingDirectory $run
    Start-Sleep -Seconds 2
    $app = Get-Process "ProductionStatistics.Wpf" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($app -eq $null) { throw "app 进程未启动" }
    # 【V2.4.1】启动即主窗，助手内部：等主窗 → 点顶栏"登录按钮" → 登录窗 → 自动登录 → 等登录窗关闭
    $ok = Invoke-UiaLogin -Proc $app -User "admin" -Pass "123456" -TimeoutMs 30000
    if (-not $ok) { throw "自动登录失败（登录窗未关闭）" }
}

function Start-Stack {
    Start-FakePlcOnce
    Start-AppWithLogin
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
    # ---------------- 阶段零：启动免登录 + 顶栏登录链路（T9 / T10 / T11 / T12）【V2.4.1】 ----------------
    Write-Output ""
    Write-Output "[阶段零] 启动免登录 + 顶栏登录（T9 错误密码 / T10 正确登录 / T11 自动选页签 / T12 免登录进主窗）..."
    Start-FakePlcOnce

    Start-Process (Join-Path $run "ProductionStatistics.Wpf.exe") -WorkingDirectory $run
    Start-Sleep -Seconds 2
    $app = Get-Process "ProductionStatistics.Wpf" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($app -eq $null) { throw "app 进程未启动" }

    # T12 启动免登录：主窗直接出现（不再先弹登录窗），日志有"主窗体初始化完成"且无"登录成功"
    $log0Text = ""
    for ($i = 0; $i -lt 20; $i++) {
        if (Test-Path $logPath) {
            $log0Text = ([System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8) -join "`n")
            if ($log0Text -match "主窗体初始化完成") { break }
        }
        Start-Sleep -Seconds 1
    }
    Assert ($log0Text -match "主窗体初始化完成") "T12 启动直接进主窗（免登录）" "日志含'主窗体初始化完成'"
    Assert (-not ($log0Text -match "登录成功: admin")) "T12 启动未自动登录" "免登录启动无'登录成功'"

    # T9 错误密码：助手先点主窗顶栏"登录按钮"打开登录窗，填 admin + 错密码点登录；
    #    登录窗应保持打开，日志记"登录失败"，且不出现"登录成功"
    Invoke-UiaLogin -Proc $app -User "admin" -Pass "wrong-pass" -NoWaitMain | Out-Null
    Start-Sleep -Seconds 2
    $log0Text = ([System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8) -join "`n")
    Assert ($log0Text -match "登录失败") "T9 错误密码被拒绝" "日志含'登录失败'"
    Assert (-not ($log0Text -match "登录成功: admin")) "T9 错误密码不登录" "错误密码后无'登录成功'"

    # T10 正确登录：登录窗还开着，助手直接复用填 admin/123456 → 登录窗关闭（DialogResult=true）
    $loginOk = Invoke-UiaLogin -Proc $app -User "admin" -Pass "123456"
    Start-Sleep -Seconds 1
    $log0Text = ([System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8) -join "`n")
    Assert ($loginOk -eq $true) "T10 正确账号登录（登录窗关闭）" "助手返回 true"
    Assert ($log0Text -match "登录成功: admin") "T10 正确账号登录成功" "日志含'登录成功: admin'"
    Assert ($log0Text -match "主窗体初始化完成") "T10 主窗仍在" "日志含'主窗体初始化完成'"

    # T11 启动自动选页签：日志序号与当前时刻应选班次一致（口径与软件 自动选班次页签() 相同）
    $hourNow = (Get-Date).Hour
    if (($hourNow -ge 8) -and ($hourNow -lt 20)) { $期望页签 = 0 }
    elseif ($hourNow -ge 20) { $期望页签 = 1 }
    else { $期望页签 = 3 }
    Assert ($log0Text -match ("启动自动选页签: 序号=" + $期望页签)) "T11 启动自动选页签正确" ("当前 " + $hourNow + " 点 → 期望序号=" + $期望页签)

    Stop-AppOnly

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

    # T6 重启无爆发（残留 ON 场景：FakePlc 继续跑，只杀软件再启；重启后同样要先过登录）
    Write-Output ""
    Write-Output "[阶段二] 重启软件（复现现场'残留 ON 重启'事故场景）..."
    Stop-AppOnly
    Start-FakePlcOnce
    Start-Process (Join-Path $run "ProductionStatistics.Wpf.exe") -WorkingDirectory $run
    Start-Sleep -Seconds 3
    $app2 = Get-Process "ProductionStatistics.Wpf" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($app2 -eq $null) { throw "重启后 app 进程未启动" }
    $ok2 = Invoke-UiaLogin -Proc $app2 -User "admin" -Pass "123456" -TimeoutMs 30000
    if (-not $ok2) { throw "重启后自动登录失败" }
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

    # T8 数据库落库（【V2.3.1】主存储从 CSV 换 SQLite：库文件存在 + 当天数据真进了库）
    Write-Output ""
    Write-Output "[T8] SQLite 落库断言..."
    $dbPath = Join-Path $run "数据\生产统计.db"
    Assert (Test-Path $dbPath) "T8 数据库文件已创建" ("路径 " + $dbPath)
    if (Test-Path $dbPath) {
        # 用部署目录里的 System.Data.SQLite 读库（x64 互操作随 bin 一并拷在 run\x64\，PS x64 直接加载）
        Add-Type -Path (Join-Path $run "System.Data.SQLite.dll")
        $cs = "Data Source=" + $dbPath + ";Version=3;"
        $db = New-Object System.Data.SQLite.SQLiteConnection -ArgumentList $cs
        $db.Open()
        $cmd = $db.CreateCommand()
        # 断言整库（运行目录全新，库内数据全部来自本轮）：注意夜班(00:00-08:00)计数归属
        # 昨天的 DayRecord（20:00 所在日期），所以不能按"今天"过滤，否则凌晨跑回归必挂
        $cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(ok1+ok2+ok3),0) FROM hour_stat"
        $reader = $cmd.ExecuteReader()
        $rows = 0; $okSum = 0
        if ($reader.Read()) { $rows = [int]$reader.GetInt64(0); $okSum = [int]$reader.GetInt64(1) }
        $reader.Close()
        $db.Close()
        Assert ($rows -ge 1) "T8 小时行已落库" ("hour_stat 行数 " + $rows)
        # 产量计数进库：脉冲源转了近 3 分钟，OK 合计必然 >0（链路：采集 → 内存 → 30 秒 UPSERT）
        Assert ($okSum -gt 0) "T8 产量计数已入库" ("三单元 OK 合计 " + $okSum + "（>0 即计数链路写进库）")
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
