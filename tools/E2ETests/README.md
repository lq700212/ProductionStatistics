# E2ETests — 生产统计端到端自动化回归

一键全量回归：**真 exe + 假 PLC（FakePlc）跑真实采集/计数/落盘链路，从运行日志提取数据做断言**。
计数逻辑（去抖/合并读/复位/首次采样）任何改动后必跑；全 PASS 才允许交付。

## 用例清单

| 用例 | 断言 | 对应现场事故 |
| --- | --- | --- |
| T1 构建通过 | `dotnet build` 0 error | —— |
| T2 授权注册 | 日志含 `HSL 授权状态: 已激活`，全程零 `系统授权失败` | 11-18/11-19 整夜授权失败断线漏计 |
| T3 合并读生效 | 启动日志 `合并读=启用`；时间账目 `合并读=1` 且单轮周期 <160ms | 4 次报文时代周期 200~330ms 漏采窗口大 |
| T4 计数与脉冲源一致 | 每分钟 `合计` ∈ [20,40]（FakePlc normal 模式 ≈33 件/分钟） | —— |
| T5 时间账目守恒 | 每分钟 `差额` < 1.0 秒 | "四类时间之和=墙上时间"验收标准 |
| T6 重启无爆发 | 杀软件重启（触发位残留 ON，复现现场场景）后分钟产量仍 ∈ [20,40] | 11-19 重启后 10 分钟计 398 件 |
| T7 全程零 ERROR | 日志零 `[ERROR` 行 | —— |

## 用法

```powershell
# 标准回归（项目根执行；需 hsl.dat 授权文件在默认位置）
powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1

# 已构建过，跳过 T1 加速
powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1 -SkipBuild

# 保留测试运行目录与日志（排障用）
powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1 -KeepOutput

# 指定授权文件位置
powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1 -HslDat "D:\path\hsl.dat"
```

退出码：`0` = 全部 PASS；`1` = 有 FAIL（失败用例名直接打印在汇总里）。

## 前置条件

- hsl.dat（HSL 授权文件，默认取 `E:\Project\HslCommunicationLicense\hsl.dat`）；
  缺失时 T2 自动降级为 SKIP，其余用例照跑。
- 端口 6060 未被占用（FakePlc 监听端口）。
- **脚本必须保持 UTF-8 with BOM + CRLF**（PS5.1 对无 BOM UTF-8 按 ANSI 解码会乱码）。
