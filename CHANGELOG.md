# 更新记录

## v0.0.3 · 2026-11-19 · 目录规范化：第三方二进制依赖统一收进 `libs/`

> 起因：仓库根目录直接躺着 `EPPlus.dll` / `HslCommunication.dll` 两个第三方二进制，
> 与 sln、README 混排；`tools/DocShot` 编译 FakePlc 又引用一份 `build\HslCommunication.dll`，
> 但没有任何"从哪来"的步骤——新环境克隆后 build 目录为空，编译直接失败。
> 本轮只调整文件放置与引用路径，不改任何运行逻辑。

### 目录调整

- 新增 `libs/` 目录：**第三方二进制依赖的唯一存放地**，用 `git mv` 移入
  `EPPlus.dll`、`HslCommunication.dll`（保留 git 历史）。
- 目录职责就此固定：`libs/` = 二进制依赖；`extern/` = 源码级依赖（kaleidoscope submodule，
  其内部 `libs/` 由子模块自己管）；`tools/` = 开发工具链（产物走 gitignore 的 `build/`）。
- 移动前三处 `HslCommunication.dll` 实测 SHA256 完全一致（原仓库根 / DocShot build / kaleidoscope libs），
  本次统一后主工程与 FakePlc 都引用 `libs/` 同一份，避免"软件用一份、假 PLC 用另一份"的版本漂移。

### 引用路径同步

- `ProductionStatistics/ProductionStatistics.csproj`：两条 `HintPath` 改为
  `..\libs\HslCommunication.dll` / `..\libs\EPPlus.dll`，并补注释说明 `libs\` 职责与
  `Private=True` 自动带出到 bin 的行为。
- `tools/E2ETests/Run-E2ETests.ps1`：编译 FakePlc 的 `/reference` 改为 `$root\libs\HslCommunication.dll`。
- `tools/DocShot/README.md`：编译 FakePlc 改为 `/r:libs\HslCommunication.dll`，
  并补上遗漏的运行时步骤——`copy libs\HslCommunication.dll tools\DocShot\build\`
  （csc 的 `/r` 只编译期引用、不复制文件，FakePlc.exe 运行时必须在同目录找到该 DLL）。
- `.gitignore`：头部目录职责注释同步（根目录 DLL → `libs/`），并修正 `build/` 那段
  "源件在仓库根"的过期说明。
- `AGENTS.md`：关键文件导航补 `libs/*.dll` 与 `extern/` 两行；FakePlc 编译命令 reference 改 `libs\`。
- `README.md`：目录结构树补 `libs/` 并注明"必须入库"。
- `docs/03_内部版_生产统计.md`：截图工具链编译命令改从项目根执行、引用 `libs\`，
  与 DocShot README 对齐（PDF 同步重新生成）。

### 验证

- `dotnet build` 0 error；构建产物 `bin\Debug\net472` 中 `HslCommunication.dll` / `EPPlus.dll`
  正常带出。
- 用新路径手工编译 `FakePlc.cs` 通过（csc exit=0）。
- 端到端回归 `tools/E2ETests/Run-E2ETests.ps1` 全量通过（7 组用例断言不变）。

## v0.0.2 · 2026-11-19 · 主窗 UI 真接入 Kaleidoscope.UI（基类 + 控件族 + 主题契约）

> 上一版只是"引用库程序集"，本轮把主窗真正落到库的基类与控件族上：
> 可迁的直接用库键，库没有的沉淀成 `Kx*` 通用件（子模块另行提交）。
> 本版定版 v0.0.2（工程版本同步 0.0.2）。

### 主窗基类迁移（MainWindow.xaml / MainWindow.xaml.cs）

- 根元素改继承 `Kaleidoscope.UI.Views.ChromeBorderlessWindow`（`CaptionHeight="0"`
  `CornerRadius="18"`），删除 `WindowStyle/AllowsTransparency/Background` 声明——
  分层真圆角三件套、WindowChrome 外壳、根 Border 圆角 Clip、
  `WM_GETMINMAXINFO` 最大化工作区修正全部由基类负责。
- **拆除伪最大化三段互锁**：删除 `伪最大化中/还原宽/还原高/应用最大化外观()/钉住内容高度()`
  旧实现；`切换最大化()` 改真 `WindowState` 切换；`窗口_StateChanged` 只更新最大化图标，
  并补上基类不管的 `Padding/BorderThickness` 归零（最大化时）。
- **钉高口径修正**：内容高度改由 `SizeChanged.NewSize.Height` 驱动（原用 `ActualHeight`，
  在 `MinHeight` 兜底时拿不到真实可视高度）；补 `double.IsNaN` 判断（`Height` 缺省 NaN 时
  `Math.Abs(NaN-x)` 恒 false、永远钉不上，迁移实测踩坑）。
- 真最大化验证：1920×1080 屏工作区 1920×1040 @(0,0)，不盖任务栏、四角圆角归零、
  还原后圆角恢复 18。

### 控件替换（MainWindow.xaml）

| 区域 | 原宿主样式 | 现库键 |
| --- | --- | --- |
| 顶栏容器 | 手写 Border | `KxTopBar` |
| PLC 连接胶囊 + 灯 + 文字 | 手写 Border/Ellipse | `StatusChip` + `KxStatusDot` + `StatusChipText`（Tag=Ok/Ng） |
| 主题开关 | `ThemeSwitch` | `KxThemeSwitch` |
| 语言切换 | `LangButton` | `KxLangButton` |
| 导出按钮 | `PrimaryButton` | `KxPrimaryButton`（实例覆盖 `Padding=16,8` `FontSize=13`） |
| 窗口三钮 | `窗口按钮`/`关闭按钮` | `KxTitleMinButton`/`KxTitleMaxButton`/`KxTitleCloseButton`（实例覆盖 34×28） |
| 班次页签 | `ShiftTab` | `KxShiftTab` |
| KPI 图标方块 | `KpiIconBox` | `KxIconBox` |
| 右栏三卡 | `Card` | `KxCard`；卡片标题 `CardTitleText` → `SectionTitle` |
| 稼动率圆环 | 手写 Ellipse | `KxRingBg` / `KxRingValue` |
| 单元 OK/NG/IDLE 徽标 | 手写弱底 Border | `StatusChip` + `StatusChipText`（Tag=Ok/Ng/无） |
| 提示框 | 手写弱底 Border | `KxInfoBox` + `KxStatusDot` + `StatusChipText`（Tag=Pri/Ng） |
| 底栏状态灯 | 手写 Ellipse | `KxStatusDot`（Tag=Ok/Warn/Ng） |

- **动态着色改语义 Tag**：`刷新提示/刷新状态条/刷新连接状态/设备状态徽标/自动中文字/报警文字`
  不再在代码里 `FindResource` 赋 `Background/BorderBrush/Fill/Foreground`，统一设
  `Tag="Ok/Warn/Ng/Pri"` 交给库样式触发器——切主题零遗漏、代码里零颜色字面量。
- KPI 卡片外壳与 DataGrid 明确**不迁**（迁移收益低、视觉差异大），保留宿主 `Card`。
- 删除宿主 `Controls/Styles.xaml` 中已被库接管的 8 个样式：`PrimaryButton` / `GhostButton` /
  `ShiftTab` / `ThemeSwitch` / `LangButton` / `窗口按钮` / `关闭按钮` / `KpiIconBox`；
  隐式 `ScrollBar` 改为 `BasedOn="{StaticResource KxSlimScrollBar}"`（单一来源，不双份并存）。

### 主题键板与切换修复

- 宿主 `Themes/Light.xaml` / `Dark.xaml` 各补一段 **Kaleidoscope.UI 契约键段**（字面值，
  禁止 `<StaticResource ResourceKey=.../>` 元素转发）：`Brush.Shell.*` 业务壳键 +
  `Fn.ShellCardRadius` + `Fx.ShellCardShadow` + 主题开关七键，两本各 133 键、键集完全一致。
- **修复主题切换静默失效**：`App.切换主题` 原来按 `Contains("/Themes/")` 匹配宿主题字典，
  但 `App.xaml` 里写的是相对 URI `Themes/Light.xaml`（无前导斜杠）→ 旧字典删不掉、
  新字典追加在末尾，表现为"日志说已切换、界面颜色不变"。改为按
  `EndsWith("Themes/Light.xaml"|"Themes/Dark.xaml")` 精确匹配，并保留 `;component`
  排除库字典。浅→深→浅运行时切换实测生效。

### 回归与沉淀

- 新增 `tools/E2ETests/Run-ThemeContractTests.ps1`（纯静态、退出码可接 CI）：
  C1 两本主题键集一致 / C2 库样式 DynamicResource 键 ⊆ 宿主两本主题 / C3 禁元素转发。
  当前基线 `PASS=9 FAIL=0`，键数 133/133。
- 目检回归：1280×900 / 1440×900 / 1500×920 三档 × 浅/深两色，1920×1080 真最大化
  （工作区 1920×1040 @(0,0)，不盖任务栏、圆角归零）浅/深两色，状态条/右栏/表格
  均完整；窗口最小高度 840 时右栏内部滚动、底部状态条始终可见。
- 库侧 `Kx*` 壳族样式（`KxThemeSwitch/KxShiftTab/KxLangButton/KxCard/KxIconBox/KxTopBar/
  KxInfoBox/KxStatusDot/KxRingBg/KxRingValue/KxSlimScrollBar`）写在 `extern/kaleidoscope`
  子模块工作区（未提交，待与宿主一并提交后更新指针；库 CHANGELOG 另记）。

### 培训文档截图流水线适配新 UI（tools/DocShot）

- **页签定位改按文本**：`TABS found=0`，页签切不动。根因是库 `KxShiftTab` 的 UIA 外框
  `Name` 为空（文本在子 TextBlock 上）、实测 231×47、窗口内 y≈95，旧实现按
  "宽>250 / 高>50 / 窗口内 y>120" 三个坐标条件筛按钮，全部不匹配。改为先按页签文本
  找 TextBlock、再向上找 Button 祖先，不再依赖尺寸与坐标。
- **保存对话框导航改走"文件名"框**：`Alt+D` 在 Win10“另存为”对话框里不激活面包屑地址栏，
  路径被打进文件名框；旧流程回车后目录能切过去，但文件名框残留路径，随后 UIA `SetValue`
  命中重建/禁用的元素抛 `ElementNotEnabledException`，导出截图中断。现改为点文件名框 →
  Ctrl+A → 输入目录 → 回车，再按"位于对话框下部、宽度>30%"筛出文件名框写文件名；
  另补搜索框残留清理：文件对话框的搜索框会跨进程记住上次搜索词，残留时列表会被
  过滤成“没有与搜索条件匹配的项”（本机只留下水印，同一入口一并清掉）。
- **截图加窗口句柄自检**：06 报警图曾偶发拍到 160×28 小窗（`mainHwnd` 瞬时失效后
  `GetWindowRect` 取到栈残值）。现截图前 `IsWindow` 校验、失效则从进程重取主窗句柄，
  `GetWindowRect` 失败或矩形 ≤0 直接报错不写垃圾图。
- **10 张截图按新 UI 全量重拍**（主界面 1500×920；09 裁边后 946×533、10 436×188），
  `postprocess.py` → `check_config.py --strict` → `check_docs.py` → `md_to_pdf.py`
  全流程通过，三份 PDF 1257/1431/1665 KB 重新生成。


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
- 提交后卫生修复：`.gitmodules` 去除 UTF-8 BOM（此前经 PowerShell 重写时带入 BOM，
  git 远端警告 `gitmodulesParse: could not parse gitmodules blob`，会导致他人 clone 后
  submodule 无法初始化——已转无 BOM UTF-8 并复验 `git submodule status` 正常）。
