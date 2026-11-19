# DocShot —— 生产统计培训文档截图与交付流水线

本目录是「生产统计（WPF 2.2.1.0）」三份培训文档的配套工具链：用**真 exe + 假 PLC**
跑真实采集/统计/落盘，再截图、后处理、核验、转 PDF。禁止 P 图，截图里的数字都来自
真实运行路径，图注统一标注「演示数据」。

产物：

- `docs/images/01..10-*.png`：10 张交付截图；
- `docs/01_操作员版_生产统计.md/.pdf`
- `docs/02_客户技术工艺版_生产统计.md/.pdf`
- `docs/03_内部版_生产统计.md/.pdf`

## 1. 文件清单

| 文件 | 作用 |
| --- | --- |
| `DocShot.cs` | 截图驱动：备份现场 → 种子数据 → 改 ini → 起假 PLC → 起真 exe → 逐场景截图 → 还原现场 |
| `FakePlc.cs` | 假 PLC：HslCommunication 12.6 `MelsecMcServer(true)`，MC 3E 二进制，默认端口 6060 |
| `seed_data.py` | 生成今天/昨天/前天的 25 行 × 14 列 CSV（UTF-8 BOM） |
| `postprocess.py` | 清 PrintWindow 四角黑弧、裁对话框 10 px 黑边，主界面等比缩到 1800 宽 |
| `check_config.py` | 核验配置的唯一事实源：功能边界、硬件、禁词、必需章节、图片白名单、ini/版本/源码常量自检 |
| `check_docs.py` | 三份文档 + 截图 + PDF 的全量核验（调用 `check_config.py`） |
| `md_to_pdf.py` | Markdown → HTML → Edge 无头打印 PDF，带 A4 版式与图注绑定 |
| `build/` | 编译产物：`DocShot.exe`、`FakePlc.exe`、`HslCommunication.dll` |

运行期还会生成 `backup_<时间戳>/`（现场备份）和 `preview/`（PDF 抽检图），
交付前按第 8 节清理。

## 2. 编译

需要 .NET Framework 4.x 的 `csc.exe`；`DocShot.cs` / `FakePlc.cs` 是无 BOM 的 UTF-8，
**必须加 `/codepage:65001`**，否则中文字符串在 GBK 代码页下会编译成乱码。

在项目根执行（路径按本机调整）：

```bat
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2

"%CSC%" /nologo /target:exe /codepage:65001 ^
  /out:tools\DocShot\build\DocShot.exe ^
  /r:"%REF%\UIAutomationClient.dll" ^
  /r:"%REF%\UIAutomationTypes.dll" ^
  /r:"%REF%\WindowsBase.dll" ^
  /r:System.Drawing.dll ^
  tools\DocShot\DocShot.cs

"%CSC%" /nologo /target:exe /codepage:65001 ^
  /out:tools\DocShot\build\FakePlc.exe ^
  /r:tools\DocShot\build\HslCommunication.dll ^
  tools\DocShot\FakePlc.cs
```

## 3. 拍摄前检查

1. 关闭现场正在运行的「生产统计」窗口（软件没有单实例互斥，双开会互相覆盖 CSV；
   `DocShot` 启动时也会尝试结束已有进程）。
2. 确认 `ProductionStatistics\bin\Debug\net472\生产统计.ini` 是现场值
   （IP、端口、语言=中文、主题=浅色、`# 不复位地址=` 处于注释状态）。
3. 确认 `docs\images` 已存在。
4. Python 在 `PATH` 中（`DocShot` 会用 `python` 调 `seed_data.py`）。

## 4. 全量截图

```bat
tools\DocShot\build\DocShot.exe ^
  --exe "ProductionStatistics\bin\Debug\net472\ProductionStatistics.Wpf.exe" ^
  --out "docs\images" ^
  --fake "tools\DocShot\build\FakePlc.exe" ^
  --seed "tools\DocShot\seed_data.py"
```

参数：

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `--exe` | 是 | 真实 WPF 主程序；数据目录、ini、logs 都按它的目录推导 |
| `--out` | 是 | 截图输出目录，图片名全 ASCII |
| `--fake` | 是 | `FakePlc.exe` |
| `--seed` | 是 | `seed_data.py` |
| `--only-export` | 否 | 只拍 09/10（导出对话框与成功提示），其余场景跳过 |

执行顺序（与 `docshot_console.txt` 的日志一致）：

1. 备份 `生产统计.ini`、`logs`、`数据` 到 `backup_<时间戳>`；
2. 写三天种子 CSV；
3. 改 ini：IP=127.0.0.1、Port=6060、语言=中文、主题=浅色、临时启用
   `不复位地址=M3212,M3222,M3232`（让待料场景的 IDLE 保持 ON）；
4. 启动假 PLC 与真 exe，等主窗口；
5. 依次拍：当天白班 → 昨天白班 → 昨天夜班 → 英文 → 深色 → 待料 → 离线 → 报警；
6. 导出 Excel，拍保存对话框（09）与成功提示（10）；
7. 关闭 exe / 假 PLC，从备份还原 ini、logs、数据。

成功标志：控制台最后一行 `ALL PASS`，退出码 0。

## 5. 只重拍导出

导出目录或文件名需要更新时，不必重拍全部场景：

```bat
tools\DocShot\build\DocShot.exe ... --only-export
```

09 图会拍到保存对话框左侧导航，**仅内部版可用**；客户两版只用 10 图。
导出落点固定为 `C:\Users\Public\Documents\ProductionHistoryExport`，
不要改回 `%TEMP%`（会带出内部工具名和用户名）。

## 6. 后处理与核验

```bat
python tools\DocShot\postprocess.py
python tools\DocShot\check_config.py --strict
python tools\DocShot\check_docs.py
```

- `postprocess.py`：只处理宽度 ≥2000 的大图做四角泛洪清近黑像素（阈值 24，
  单角上限 5000）；所有图片先裁掉整行/整列 99% 近黑的外圈（09/10 各有 10 px），
  再把主界面等比缩到 1800 宽；对话框不缩放。
- `check_config.py --strict`：ini 现场值、工程版本 2.2.1.0、源码常量抽查；
  失败返回非 0，可单独当“拍摄后现场是否还原”的守门员。
- `check_docs.py`：文档编码/字数/表格/图片/章节/禁词/否定语境/PLC 地址键/
  内部版源码定位/PDF 体积与文件头/HTML 残留，一次跑完。

## 7. 生成 PDF

```bat
python tools\DocShot\md_to_pdf.py                # 三份全转，成功后删 docs\_preview
python tools\DocShot\md_to_pdf.py --keep-html    # 保留 HTML 供浏览器预览
python tools\DocShot\md_to_pdf.py 01_操作员版_生产统计.md
```

依赖：Python `markdown`，以及 Microsoft Edge（无头 `--print-to-pdf`）。
PDF 必须 >50 KB、文件头为 `%PDF-`；`check_docs.py` 会复核。

## 8. 已知坑（都是实际踩过的）

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 截图尺寸/点击坐标全错 | 系统 150% DPI，进程未声明感知 | `SetProcessDPIAware()`（程序入口已调用） |
| 主界面四角有黑弧 / 对话框四周有黑框 | 圆角无边框窗 + `PrintWindow` 把透明区和窗口阴影画成黑 | `postprocess.py` 四角泛洪清理，并裁掉整行/整列的 10 px 黑边 |
| UIA `Invoke()` 后整个流程卡死 | Invoke 会同步等到模态框关闭 | 导出/保存按钮放后台线程 `InvokeAsync()`；页签、语言、主题用鼠标点击 |
| 保存对话框弹“确认另存为” | 目标目录已有同名 xlsx | 导出前先删同名文件 |
| 待料场景一闪而过 | 软件读到 IDLE 后会复位自锁位 | ini 临时加 `不复位地址=M3212,M3222,M3232`，拍完必须还原 |
| 离线截图顶栏/底栏不一致 | 两处刷新周期不同 | 等顶栏和底栏都显示“连接失败”再拍 |
| 报警提示显示 0.0 分钟 | 报警时长按分钟累计，刚触发还没满 0.1 | 报警后多等 6 秒再拍 |
| 图注单独跑到下一页 | Markdown 的图与图注是两个 `<p>`，只给 `img` 设 `break-inside` 不够 | `md_to_pdf.py` 把「图片 + 斜体图注」合并成 `<figure>`，整块不可拆 |
| 客户文档出现内部路径/用户名 | 截图落点用了 `%TEMP%` 或用户“文档” | 导出落点改公共文档；09 图登记为内部专用，客户版引用直接 FAIL |
| CSV 被 Excel 打开乱码 | 没写 UTF-8 BOM | `seed_data.py` 固定 `utf-8-sig`，25 行 × 14 列 |
| 文档里出现 `truncated` / `TODO` | 分段写作的截断残留 | `check_docs.py` 通用禁词直接 FAIL |

## 9. 交付前清理与还原

1. 删除 `backup_*`（确认当前 ini/数据/日志与备份哈希一致后再删）；
2. 删除 `preview/`、`docs/_parts/`、`docs/_preview/`；
3. 删除本目录下的运行日志 `docshot.log`、`docshot_console*.txt`；
4. 确认 `bin\Debug\net472\生产统计.ini` 已恢复现场值，且没有 `127.0.0.1`；
5. 确认没有残留 `DocShot.exe` / `FakePlc.exe` / `ProductionStatistics.Wpf.exe` 进程；
6. 确认 `C:\Users\Public\Documents\ProductionHistoryExport` 已删除；
7. 最后跑一遍 `check_config.py --strict` 与 `check_docs.py`，必须全绿。
