# 更新记录

## v0.0.1 · 2026-11-19 · 计数可靠性根治 + kaleidoscope submodule 接入 + 工程文档门户化

> 本版起 CHANGELOG 重开（历史记录见 git 历史）。版本定版 v0.0.1。

### 现场事故根因（客户反馈"总数跟单站对不上"与"暂停一晚上第二天不重启就不计数"）

两份现场日志（11-18 / 11-19）取证结论，三个 bug 叠加：

1. **HSL 授权缺失（主根因）**：HslCommunication 未注册授权码，PLC 通讯间歇性返回
   "系统授权失败"——11-18 日志 00:00~08:25 整夜断线、11-19 日志 08:20 断线直到 10:51
   客户重启才恢复。断线时段产量整段漏计且不可恢复；"重启才恢复"恰好赶上授权失效窗口，
   造成"暂停一晚上第二天必须重启"的假象（与暂停无关）。**已确认现场忘配授权码。**
2. **重启后残留 ON 爆发重复计数**：断线期间软件无法复位自锁位，触发位挂着 ON 过夜；
   重启后去抖状态按"残留 ON=刚发生过"基准同步本应压住，但现场实测 10:52~10:57
   每分钟计 62~73 件（正常节拍 1~6 件），11:00 结算白班02 = OK 398 件/生产 8.6 分钟——
   老去抖按"连续 2 轮 OFF"确认，报文周期抖动下 2 轮 OFF 的真实时长在 300~700ms 漂移，
   复位空档被凑够 2 轮的相位组合打穿，同一件被反复计数。
3. **复位-再置位竞态漏计**：日志反复出现"复位后重新置位间隔约 230ms ≈ 单轮采样周期 230ms"；
   PLC 重新置位快于采样周期时，软件的复位写 false 吃掉 PLC 刚置位的下一件 → 漏计
   （NG 件少、每件都被盯，客户感知"NG 不计数"最明显）。

### 计数可靠性修复（Services/PlcCollector.cs、Business/TriggerDebouncer.cs、Business/PlcAddressConfig.cs）

- **授权统一注册**：接入 kaleidoscope `HslAuthorization.EnsureRegistered()`，启动时从 exe
  同目录 `hsl.dat`（ENC1 加密格式）读取授权码并调 HSL `SetAuthorizationCode`（本地验签、幂等）；
  注册结果写日志（`HSL 授权状态: 已激活/未激活`），未激活时明示后果。
  `hsl.dat` 为商业机密，已入 `.gitignore`（连同 `HslLicense.dat`），严禁入库。
- **去抖口径改时间制**：`TriggerDebouncer` 的"确认OFF轮数"（轮数×名义周期，受报文抖动漂移）
  改为"OFF 持续时长 ≥ `信号确认OFF毫秒`"（Stopwatch 单调时钟量时长，默认 300ms =
  现场实测复位空档 230ms + 30% 余量，ini 可调）。旧 ini 键 `信号确认OFF轮数` 退役。
- **11 位合并读**：`PlcAddressConfig` 新增合并读算术（同前缀 + 跨度 ≤100 点判定、偏移表）；
  采集循环优先一次 `ReadBool(起始, 跨度)` 读回全部 11 个信号位再拆包，地址不满足自动回落
  4 次报文老路径。实测单轮周期 200~330ms → **109ms**，复位空档可见性翻倍，漏采窗口减半。
- **死代码修复**：`本分钟上升沿` 计数器从未累加（"计数速率异常"告警永不触发）——补上累加；
  删除 `去抖器.最小计时间隔秒` 重复赋值行。
- **时间账目日志加 `合并读=0/1` 字段**：现场一眼确认 1 次报文模式是否在跑。

### kaleidoscope submodule 接入（extern/kaleidoscope）

- 公司通讯/UI 通用库以 git submodule 接入 `extern/kaleidoscope`，主工程 ProjectReference
  直连源码（`Kaleidoscope.csproj` net472 + `Kaleidoscope.UI.csproj` 多目标取 net472 产物），
  改 submodule 后重编主工程即生效。
- **通讯侧**：本工程用其 `HslAuthorization`（授权统一注册）与 `LogHelper`（库内日志桥接到
  本工程运行日志，一份日志看全）；采集循环暂保留自管 `MelsecMcNet`（合并读为专用优化），
  换品牌/加设备时优先复用其 `HslPlcClient`（约定写入 AGENTS.md）。
- **UI 侧**：`App.xaml` 合并 `Kaleidoscope.UI` 缺省键板（DefaultLight.xaml）与控件族
  （FreshStyles/ShellStyles，全 keyed 样式），本工程主题字典在后位覆盖同名键。
  新弹窗/按钮/输入优先用库内控件（约定写入 AGENTS.md）。
- **修复主题切换误删字典 bug**：`App.切换主题` 按 `Contains("/Themes/")` 匹配删除会把
  kaleidoscope 的键板字典一并删掉（pack URI 同含 /Themes/）——加 `;component` 排除，
  只删本工程主题字典。

### 配置对齐现场

- ini 缺省模板对齐现场《生产统计.ini》附件：IP 缺省 192.168.1.156、新增
  `信号确认OFF毫秒`（缺省 300）与注释、`轮询间隔Ms=100`、`不复位地址` 注释齐全；
  旧键 `信号确认OFF轮数` 退役（读取端换毫秒口径，无存量包袱）。
- `MainWindow` IP 缺省值同步 192.168.1.156。

### 测试沉淀（tools/E2ETests/）

- 新增端到端自动化回归 `Run-E2ETests.ps1`（+README）：真 exe + FakePlc 假 PLC 跑真实
  采集/计数/落盘链路，从运行日志提取数据断言。7 组用例 12 断言：
  T1 构建 / T2 授权注册（已激活 + 全程零"系统授权失败"）/ T3 合并读（启用 + 生效 +
  单轮周期 <160ms）/ T4 计数与脉冲源一致（合计 ∈ [20,40]，脉冲源 ≈33 件/分钟）/
  T5 时间账目守恒（差额 <1s）/ T6 重启无爆发（残留 ON 场景复现现场 398 件事故，
  重启后分钟产量仍在带宽内）/ T7 全程零 ERROR。
- 实测 **PASS=12 FAIL=0**；测试约定入 AGENTS.md 铁律 3.1（新用例一律沉淀为脚本）。

### 工程文档

- **AGENTS.md 重写**：从 HuaJiVision 项目副本改为本项目的真实指南——角色/技术栈
  （kaleidoscope 接入方式）/仓库与提交（submodule 流程）/铁律（机密、构建+回归、
  测试沉淀、UI 线程纪律、语言两处同步、口径确认）/计数可靠性约定（自锁位/去抖/
  合并读/首次采样/诊断日志口诀）/关键文件导航/构建与验证命令/文档同步。
- **README.md 重写为门户模式**（参考 HJVision）：居中标题+徽章、界面速览、快速开始
  （含 submodule 还原）、功能特性、架构、目录结构、文档导航、配置与授权（含 hsl.dat
  机密红线）、部署交付、注意事项。
- `.gitignore` 补 `hsl.dat` / `HslLicense.dat`（机密）、`Logs/`（kaleidoscope 日志桥兜底）。

### 验证

- 构建 0 error（MSB3245 为 kaleidoscope 库内串口引用固有警告，本工程不用串口服务）。
- E2E 回归 12 PASS / 0 FAIL（含授权、合并读、计数带宽、时间守恒、重启无爆发）。
- UI 冒烟：合并 Kaleidoscope.UI 字典后主窗正常启动，深浅主题切换正常。
