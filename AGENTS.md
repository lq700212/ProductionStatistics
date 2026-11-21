# AGENTS.md — 生产统计（点胶固化收料机）项目指南

> 本文件是 AI 助手在操作本项目前的**强制前置阅读**。开工前先读本文件，明确角色、约定与红线。
> 优先级：本文档 > 项目已有代码风格 > 通用最佳实践。

## 项目角色

你是本项目（WPF 生产统计看板）的**资深维护工程师**，负责按用户需求改代码、修 bug、沉淀约定。
改动必须**可编译、可运行、风格统一**，并在关键改动后更新 `CHANGELOG.md` 与 `README.md`。

## 项目一句话

三菱 FX5U 以太网采集 → 三单元（焊接/气密/贴标）OK·NG 计数 + 班次时间记账 → SQLite 数据库落库【V2.3.1】→ Excel/CSV 导出。
现场运行 exe = `ProductionStatistics.Wpf.exe`（程序集名保留 .Wpf 后缀，工程目录已去掉）。

## 技术栈与结构

- **.NET Framework 4.7.2 + WPF**（SDK-style csproj + UseWPF；`LangVersion 7.3`）；
- **HslCommunication 12.6**（三菱 MC 3E 帧，商业授权，授权文件 `hsl.dat` 见"商业机密红线"）；
- **kaleidoscope**（`extern/kaleidoscope` git submodule，公司通讯/UI 通用库）：
  - 通讯侧：本工程目前直用 HSL 的 `MelsecMcNet`（采集循环自管），kaleidoscope 的
    `HslAuthorization`（授权统一注册）与 `LogHelper`（日志桥）已接入；
    若要做"换品牌/加设备"，优先复用 `HslPlcClient`（多品牌主站封装：惰性连接+心跳自愈+自动轮询），
    **不要再手写连接管理**；
  - UI 侧：`App.xaml` 已合并 `Kaleidoscope.UI` 的缺省键板（`DefaultLight.xaml`）与控件族
    （`FreshStyles.xaml` / `ShellStyles.xaml`，全 keyed 样式）。新弹窗优先继承
    `ChromeDialogWindow`，新按钮/输入优先用 `FreshButton` / `FreshInput` 族样式，
    语义键契约见 `extern/kaleidoscope/Kaleidoscope.UI/README.md`；
- Excel 导出：EPPlus；日志：自研 `Business/Log.cs`（按天分文件，ERROR 镜像 `错误日志.txt`）；
- `tools/DocShot/`：文档截图 harness（FakePlc 假 PLC + 真 exe 截图，禁止 P 图）。

## 仓库与提交

- 远程 `origin = https://github.com/lq700212/ProductionStatistics.git`。
- **submodule**：`extern/kaleidoscope` → https://github.com/lq700212/kaleidoscope.git 。
  克隆仓库后先 `git submodule update --init --recursive` 再构建；主工程以 ProjectReference
  直连 submodule 源码，submodule 有改动时在子模块里 commit 后回到主仓库更新指针再一并提交。
- **不主动 commit/push**，除非用户明确要求；提交前先 `git status` + `git diff` 确认只包含预期改动。
- commit message 走全局技能 `git-auto-commit-push`（从 CHANGELOG 最新小节自动生成），**禁止 AI 手打 message 正文**；
  用 `--explicit` 显式暂存模式，逐个 `git add <路径>`，**禁止 `git add -A` 盲加**（会把机密/临时文件带进去）。

## 铁律（违反即返工）

1. **文件编码必须是 UTF-8**。**禁止**用 PowerShell `Add-Content` / `Out-File` 默认编码写含中文的文件
   （会写成 GBK）。写文件用 write 工具；仅追加/改一行用 edit 工具；`生产统计.ini` 与 CSV 一律 UTF-8(BOM)。
2. **商业机密红线**：`hsl.dat`（HSL 授权码，kaleidoscope `HslAuthorization` 格式，exe 同目录读取）
   已入 `.gitignore`——**严禁入库/上传/分发/写进任何文档**；部署新版本必须同步拷贝 `hsl.dat` 到 exe 目录，
   缺它 PLC 会间歇性"系统授权失败"→ 断线漏计（V2.2.2 现场 11-18/11-19 事故根因）。
3. **改动后必须构建验证**：`dotnet build ProductionStatistics\ProductionStatistics.csproj -c Debug`，
   禁止交付编译不过的代码。涉及计数逻辑的改动必须跑端到端回归
   `powershell -ExecutionPolicy Bypass -File tools\E2ETests\Run-E2ETests.ps1`，**禁止只看编译过就交付**。
3.1 **测试用例必须沉淀固化**：任何新写/临时写的测试（断言脚本、复现场景、验证步骤），
   完成后立即评估能否沉淀为 `tools/` 下可重复执行的脚本——能脚本化的一律脚本化
   （参考 `tools/E2ETests/Run-E2ETests.ps1` 的用例表 + 断言 + 汇总模式），
   **禁止只留在对话里**，让后续回归直接跑脚本、不重新编写用例。
4. **UI 线程禁做网络 IO**：采集线程只写内存 + 置脏标志，界面全部由 1 秒 DispatcherTimer 消费刷新；
   采集线程与 UI 通过 `StatisticsStore.锁` 同步，**锁内禁止网络 IO**（复位写 PLC 在锁外做）。
5. **代码注释要详细，让小白能看懂学会**：关键方法写清"做什么 + 为什么 + 怎么改"，杜绝废话注释。
   改动带版本标记（如【V2.2.2】，见 CHANGELOG 对应小节）。
6. **终端有 GBK 编码显示假象，判定文件内容禁止肉眼判码**：用严格解码器
   `New-Object Text.UTF8Encoding($false,$true)` 或码位级断言
   `$t.Contains([string][char]0x4FEE)`；无 BOM 的 UTF-8 ps1 禁止 `powershell.exe -File` 直接跑。
7. **改动默认不考虑向后兼容、改彻底**：删旧逻辑不保留新旧兼容分支（旧 ini 键、旧调用点同步改掉）；
   除非用户明确要求兼容，拿不准时先问。
8. **界面语言两处同步**：静态文字在 `应用语言()` 集中改；动态文字（状态灯/合计行/状态条）在各自
   刷新方法里用 `英文界面` 三目选择——漏一处切语言时该词不跟着变。
9. **统计口径改动必须先确认**：四类时间（生产/待料/报警/停止）互斥且完全覆盖，"四类之和=墙上时间"
   是验收标准；"自动中=1 但三单元全 IDLE 记生产还是记待料"属口径定义，改前必须与用户/客户确认。
10. **复用优先，造轮子必沉淀**：接到任何需求（UI 控件/样式/令牌、通讯封装、通用工具），先查
    `extern/kaleidoscope`（UI 语义键/控件族见 `Kaleidoscope.UI/README.md`；通讯复用
    `HslPlcClient`）与本项目既有实现，**能复用尽量复用，禁止重复造轮子**；确实没有才从 0
    自造，造完必须评估通用性——具备跨项目价值的沉淀进 kaleidoscope 子模块（令牌/样式/控件/
    助手 + 其 README/CHANGELOG 同步），走子模块提交流程，供其他项目复用。

## 计数可靠性约定（本项目核心，改动前必读）

- 触发位是**自锁位**：软件读到 ON 后写回 false（`PlcSignalResetter`）；
  `设备自动中` / `设备报警` 是状态信号不复位（`不复位地址` 可配）。
- **去抖双条件**（`TriggerDebouncer`）：①OFF 持续 ≥ `信号确认OFF毫秒`（默认 300，按单调时钟量时长，
  不是轮数）；②距上次计数 ≥ `计数最小间隔Ms`（默认 1500，循环里自动抬高到 ≥2.5×采样周期）。
- **合并读**：11 个信号位（自动中/报警 + 三单元 OK/NG/IDLE）地址同前缀且跨度 ≤100 点时
  一次 `ReadBool(起始, 跨度)` 读回（`PlcAddressConfig.可一次合并读()`）；不满足自动回落 4 次报文。
  单轮周期实测 109ms（4 次报文时代 200~330ms），漏采窗口减半。
- **首次采样只作基准不计数**：启动残留 ON 不算产量，但必须复位，否则挡住后面的上升沿。
- 日志三大自诊断输出（现场排障口诀）：`时间账目`（每分钟，差额≠0 说明丢时间）、
  `本分钟产量`（每分钟，对照 PLC 侧件数）、`自锁位过密/脉冲过窄`（5 分钟节流，出现即说明
  节拍快于采样周期或脉冲太窄，先调小 `轮询间隔Ms`）。

## 代码约定

- 类/方法/属性 PascalCase；**本工程业务标识符大量使用中文命名**（如 `采集循环` / `触发信号名` /
  `读单元`），跟随既有风格，新增代码保持一致；接口前缀 `I`。
- WPF 控件命名用中文 x:Name（`提示框` / `状态灯` / `表格行` 等），跟随现状。
- 主窗体 `MainWindow.xaml.cs` 按 region 组织：统计数据与采集 / 界面数据集合 / 刷新方法群 /
  自绘窗口；新增逻辑归入对应 region。
- 界面文件头注释必须带说明（做什么 + 运行流程 + 线程纪律），参考 `MainWindow.xaml.cs` 头注释。
- **UI 资源优先取自 Kaleidoscope.UI 控件库**（keyed 样式 + 语义键），本工程 `Themes/` 只放
  两套主题色板；需要新控件形态先查 `extern/kaleidoscope/Kaleidoscope.UI/README.md` 再自绘。

## 关键文件导航

| 文件 | 作用 |
| --- | --- |
| `ProductionStatistics/MainWindow.xaml(.cs)` | 主窗体：7 页签看板（6 班次 + 历史查询【V2.3.1】）、KPI、单元状态、自绘窗口 |
| `ProductionStatistics/Services/PlcCollector.cs` | 采集线程：合并读/逐点读、上升沿计数、时间记账、诊断日志 |
| `ProductionStatistics/Services/StatisticsStore.cs` | 统计仓库：内存记录 + 30 秒 SQLite 落库【V2.3.1】（锁内拷快照、锁外写库） |
| `ProductionStatistics/Services/StatisticsDbStore.cs` | SQLite 库仓库【V2.3.1】：建库/UPSERT/读取/区间查询/历史 CSV 一次性导入（主存储 `数据\生产统计.db`，WAL；库内 `_库锁` 串行化，采集线程落库与 UI 线程查询并发安全） |
| `ProductionStatistics/LoginWindow.xaml(.cs)` | 登录窗【V2.4.0/V2.4.1】：主窗顶栏"登录"按钮按需弹出（启动免登录，登录只做身份确认） |
| `ProductionStatistics/Business/LoginService.cs` | 登录验证【V2.4.0】：预置账号 admin/123456（写死源码），成败写日志 |
| `ProductionStatistics/Services/AppConfig.cs` | ini 载入/缺省生成/主题写回（单例缓存） |
| `ProductionStatistics/Services/ExcelExporter.cs` | Excel 导出（EPPlus，27 行 × 10 列） |
| `ProductionStatistics/Business/TriggerDebouncer.cs` | 去抖器：OFF 持续时长确认 + 最小计时间隔（纯逻辑可单测） |
| `ProductionStatistics/Business/PlcSignalResetter.cs` | 自锁位复位器（不复位地址集合） |
| `ProductionStatistics/Business/PlcAddressConfig.cs` | 地址配置 + 合并读偏移表（11 位一次报文的地址算术） |
| `ProductionStatistics/Business/ShiftStore.cs` | HourStat/DayRecord + CSV 解析（旧数据导入源）与查询导出格式（InvariantCulture 固定小数点；实时写 CSV 已移除【V2.3.1】） |
| `ProductionStatistics/Business/Log.cs` | 按天滚动日志 + ERROR 镜像 |
| `ProductionStatistics/Themes/Light|Dark.xaml` | 两套主题色板（键名一致，切主题=换字典） |
| `extern/kaleidoscope/` | 通讯/UI 通用库（submodule，改动走子模块自己的提交流程） |
| `tools/DocShot/` | 文档截图 harness（FakePlc 假 PLC / Invoke-UiaLogin 自动登录 / 核验脚本 / PDF 转换） |
| `libs/HslCommunication.dll` | 三菱 MC 3E 通讯库（商业授权；csproj 与 tools 的 FakePlc 都引用这一份，禁止各处私拷贝） |
| `libs/EPPlus.dll` | Excel 导出库（4.x LGPL 版，不要升 5+ 商业授权版） |
| `extern/` | 源码级依赖目录（当前只有 kaleidoscope submodule，其 libs/ 由子模块自己管） |
| `部署运行目录（非仓库文件）` | exe 运行需带：`HslCommunication.dll`、`EPPlus.dll`、`System.Data.SQLite.dll` + `x64\`/`x86\` 的 `SQLite.Interop.dll`【V2.3.1】、`Kaleidoscope.dll`、`Kaleidoscope.UI.dll`、`hsl.dat`、`生产统计.ini`；数据主存储 `数据\生产统计.db`（SQLite，首启自动建库并导入旧 CSV） |

## 构建与验证命令

```powershell
# 构建（submodule 已就位的前提下）
dotnet build ProductionStatistics\ProductionStatistics.csproj -c Debug
# 产物：ProductionStatistics\bin\Debug\net472\ProductionStatistics.Wpf.exe

# 端到端回归（计数逻辑改动后必跑）：假 PLC + 真 exe
# ① 编译 FakePlc（注意 /codepage:65001，否则中文字符串乱码）
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /codepage:65001 /target:exe /out:tools\DocShot\build\FakePlc.exe `
  /reference:"E:\Project\ProductionStatistics\libs\HslCommunication.dll" tools\DocShot\FakePlc.cs
# ② 部署测试目录：bin\Debug\net472 全套 + hsl.dat + 生产统计.ini（IP 改 127.0.0.1）
# ③ 起 FakePlc（6060）→ 起软件 → 看 logs：授权激活/合并读=1/时间账目差额=0/
#    本分钟产量≈33（脉冲源节拍 1.8s×3 单元）
# ④ 重启软件（FakePlc 不动）复现"残留 ON 重启"场景：重启后无产量爆发（分钟计数不超脉冲源）
```

- 成功标准：编译 0 error（MSB3245 System.IO.Ports 警告来自 kaleidoscope 库内串口引用，本工程
  不用串口服务，可忽略）；端到端"时间账目差额≈0 + 分钟产量与脉冲源一致 + 重启无爆发"。

## 文档同步（每次任务完成必做，逐条核对）

- **`CHANGELOG.md`**：顶部新增或更新当前版本小节（改动范围 + 为什么 + 优化点）。
  版本号规则：没 commit 前同一轮工作只追加 `###` 小节、不加版本号；只有 commit 时才定版。
- **`README.md`**：门户模式（界面速览/快速开始/功能特性/架构/目录结构/文档导航/配置与授权/
  部署交付/注意事项），新增功能、ini 键、部署文件（hsl.dat、Kaleidoscope DLL）、submodule
  变化任一发生即同步。
- **`docs/` 三份培训文档**：界面或配置项改动后用 `tools/DocShot/` 重跑截图并同步文档。
- **测试沉淀**：新增测试场景（无论来自 bug 复现还是验收要求）同步更新
  `tools/E2ETests/Run-E2ETests.ps1` 的用例表与断言，并更新其 `README.md` 的用例清单。
- **代码注释**：详细到小白能看懂，带版本标记，中文 UTF-8，写完自查编码。
- **提交前自检**：`git status` + `git diff` 确认范围与文档同步；用户不要求 commit 时只留工作区改动。
