using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace DocShot
{
    /// <summary>
    /// 生产统计软件截图驱动（真进程 + 假 PLC + UI Automation 点击 + PrintWindow）。
    ///
    /// 流程：备份现场数据/配置 → 写三天种子 CSV → 改 ini 指向本机假 PLC →
    ///       启动假 PLC → 启动真实 exe → 逐场景截图 → 还原现场。
    ///
    /// 用法：
    ///   DocShot.exe --exe "…\ProductionStatistics.Wpf.exe" --out "…\docs\images"
    ///               --fake "…\FakePlc.exe" --seed "…\seed_data.py"
    /// 说明：
    ///   · 截图目录必须已存在；图片名全 ASCII（核验脚本的正则只认 ASCII 文件名）。
    ///   · 全程无人值守：看门狗自动点掉未知模态框，单张失败不中断整批。
    ///   · 拍完必须还原：数据目录、logs、生产统计.ini 都从备份恢复。
    /// </summary>
    internal static class DocShot
    {
        // ---------- Win32 ----------
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hDlg, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_CLOSE = 0x0010;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        // ---------- 状态 ----------
        private static string exePath, exeDir, outDir, dataDir, iniPath, logsDir;
        private static string fakeExe, seedScript, backupDir, modeFile;
        private static Process app, fake;
        private static IntPtr mainHwnd;
        private static int failCount = 0;
        private static readonly object logLock = new object();
        private static volatile bool watching = true;
        private static readonly HashSet<IntPtr> knownDialogs = new HashSet<IntPtr>();

        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            bool onlyExport = false;
            foreach (string a in args) if (a == "--only-export") onlyExport = true;
            string err = ParseArgs(args);
            if (err != null) { Console.WriteLine("ARGS ERROR: " + err); return 2; }

            Console.WriteLine("=== DocShot start ===");
            Console.WriteLine("exe=" + exePath);
            Console.WriteLine("out=" + outDir);
            Console.WriteLine("data=" + dataDir);

            Directory.CreateDirectory(outDir);
            backupDir = Path.Combine(Path.GetDirectoryName(seedScript), "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            modeFile = Path.Combine(Path.GetTempPath(), "docshot_mode.txt");

            try
            {
                KillExistingApp();
                Backup();
                WriteMode("normal");
                RunSeed();
                PatchIni();
                StartFake();
                StartApp();
                WaitMainWindow();
                Thread.Sleep(6000); // 让真实采集线程跑几轮，界面有实时状态

                Watchdog();

                if (!onlyExport)
                {
                    Shot("01-main-today-day.png", "当天白班（正常生产）");
                    ClickTab(2); Shot("02-main-yesterday-day.png", "昨天白班（切换页签）");
                    ClickTab(3); Shot("03-main-yesterday-night.png", "昨天夜班（切换页签）");
                    ClickTab(0);

                    ClickAid("语言英文"); Shot("04-main-english.png", "英文界面");
                    ClickAid("语言中文");

                    ClickAid("主题开关"); Shot("05-main-dark.png", "深色主题");
                    ClickAid("主题开关");

                    WriteMode("idle");
                    WaitTextCount("空闲", 3, 8000);
                    Shot("07-main-idle.png", "单元待料状态（三单元 IDLE 亮，显示空闲）");
                    WriteMode("normal"); Thread.Sleep(2000);

                    // 离线先拍：此时本时段还没有报警记录，提示卡保持“本时段无报警”，
                    // 不会出现“离线”与“报警 0.0 分钟”并存的误导画面。
                    StopFake();
                    // 顶栏和底栏分别由“连接状态事件”和“1 秒定时器”刷新，可能差一拍；
                    // 两处都显示失败再拍，避免出现“顶栏离线、底栏运行中”的自相矛盾截图。
                    WaitAidText("连接状态文字", "连接失败", 15000);
                    WaitAidText("状态条文字", "连接失败", 15000);
                    Thread.Sleep(1200);
                    Shot("08-main-offline.png", "PLC 离线状态");
                    StartFake();
                    WaitAidText("连接状态文字", "已连接", 15000);
                    Thread.Sleep(2000);

                    WriteMode("alarm");
                    WaitAidText("状态条文字", "报警中", 8000);
                    Thread.Sleep(6000); // 让本时段报警时间累计到 0.1 分钟，提示卡不显示 0.0
                    Shot("06-main-alarm.png", "设备报警状态");
                    WriteMode("normal"); Thread.Sleep(2000);
                }
                else
                {
                    Thread.Sleep(3000);
                }

                DoExport();
            }
            catch (Exception ex)
            {
                Log("FATAL " + ex);
                failCount++;
            }
            finally
            {
                watching = false;
                try { if (app != null && !app.HasExited) { app.CloseMainWindow(); Thread.Sleep(1500); if (!app.HasExited) app.Kill(); } } catch { }
                try { StopFake(); } catch { }
                try { Restore(); } catch (Exception ex) { Log("RESTORE ERROR " + ex.Message); failCount++; }
            }

            Console.WriteLine(failCount == 0 ? "ALL PASS" : "FAILURES=" + failCount);
            return failCount == 0 ? 0 : 1;
        }

        // ===================== 参数 =====================
        private static string ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string v = (i + 1 < args.Length) ? args[i + 1] : null;
                if (a == "--exe") { exePath = v; i++; }
                else if (a == "--out") { outDir = v; i++; }
                else if (a == "--fake") { fakeExe = v; i++; }
                else if (a == "--seed") { seedScript = v; i++; }
            }
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(outDir) ||
                string.IsNullOrEmpty(fakeExe) || string.IsNullOrEmpty(seedScript))
                return "需要 --exe --out --fake --seed";
            exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            dataDir = Path.Combine(exeDir, "数据");
            iniPath = Path.Combine(exeDir, "生产统计.ini");
            logsDir = Path.Combine(exeDir, "logs");
            if (!File.Exists(exePath)) return "exe 不存在: " + exePath;
            if (!File.Exists(fakeExe)) return "FakePlc.exe 不存在: " + fakeExe;
            if (!File.Exists(seedScript)) return "seed_data.py 不存在: " + seedScript;
            if (!File.Exists(iniPath)) return "生产统计.ini 不存在: " + iniPath;
            exePath = Path.GetFullPath(exePath);
            outDir = Path.GetFullPath(outDir);
            fakeExe = Path.GetFullPath(fakeExe);
            seedScript = Path.GetFullPath(seedScript);
            exeDir = Path.GetDirectoryName(exePath);
            dataDir = Path.Combine(exeDir, "数据");
            iniPath = Path.Combine(exeDir, "生产统计.ini");
            logsDir = Path.Combine(exeDir, "logs");
            return null;
        }

        // ===================== 准备/还原 =====================
        private static void Backup()
        {
            Directory.CreateDirectory(backupDir);
            File.Copy(iniPath, Path.Combine(backupDir, "生产统计.ini"), true);
            CopyDir(dataDir, Path.Combine(backupDir, "数据"));
            CopyDir(logsDir, Path.Combine(backupDir, "logs"));
            Log("BACKUP " + backupDir);
        }

        private static void Restore()
        {
            if (!Directory.Exists(backupDir)) return;
            File.Copy(Path.Combine(backupDir, "生产统计.ini"), iniPath, true);
            ClearDir(dataDir);
            CopyDir(Path.Combine(backupDir, "数据"), dataDir);
            ClearDir(logsDir);
            CopyDir(Path.Combine(backupDir, "logs"), logsDir);
            Log("RESTORE done");
        }

        private static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }

        private static void ClearDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
        }

        private static void RunSeed()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "\"" + seedScript + "\" --out \"" + dataDir + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(seedScript)
            };
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                Log("SEED exit=" + p.ExitCode + " " + so.Trim() + " " + se.Trim());
                if (p.ExitCode != 0) throw new Exception("种子数据生成失败: " + se);
            }
        }

        private static void PatchIni()
        {
            var lines = new List<string>(File.ReadAllLines(iniPath, Encoding.UTF8));
            Patch(lines, "IP=", "127.0.0.1");
            Patch(lines, "Port=", "6060");
            Patch(lines, "语言=", "中文");
            Patch(lines, "主题=", "浅色");
            // 演示"单元待料"时必须让 IDLE 位保持 ON：默认 9 个单元信号都是自锁位，
            // 软件读到 true 会立刻写回 false，界面就永远看不到"空闲"。
            // 这里把三个 IDLE 地址登记为"不复位地址"（真实生产配置按现场信号类型决定，
            // 拍完 Restore() 会连 ini 一起还原，不会污染现场）。
            PatchNoReset(lines, "M3212,M3222,M3232");
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(true));
            Log("PATCH ini: IP=127.0.0.1 Port=6060 语言=中文 主题=浅色 不复位地址=M3212,M3222,M3232");
        }

        /// <summary>
        /// 把 [PLC地址] 段里的“不复位地址”设为指定值。
        /// 现场 ini 里这一行通常是注释（# 不复位地址=），不能简单追加到文件末尾——
        /// 追加会落到最后一个节之外，读配置时直接忽略。
        /// </summary>
        private static void PatchNoReset(List<string> lines, string value)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t == "# 不复位地址=" || t.StartsWith("不复位地址=", StringComparison.Ordinal))
                {
                    lines[i] = "不复位地址=" + value;
                    return;
                }
            }
            // 没找到就插到 [PLC地址] 段末尾（下一个 [ 节之前）
            int 节 = lines.FindIndex(delegate(string s) { return s.Trim() == "[PLC地址]"; });
            if (节 < 0) { lines.Add("[PLC地址]"); lines.Add("不复位地址=" + value); return; }
            int 插入 = lines.Count;
            for (int i = 节 + 1; i < lines.Count; i++)
                if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal)) { 插入 = i; break; }
            lines.Insert(插入, "不复位地址=" + value);
        }

        private static void Patch(List<string> lines, string prefix, string value)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith(prefix, StringComparison.Ordinal))
                {
                    lines[i] = prefix + value;
                    return;
                }
            }
            lines.Add(prefix + value);
        }

        // ===================== 进程 =====================
        private static void KillExistingApp()
        {
            foreach (var p in Process.GetProcessesByName("ProductionStatistics.Wpf"))
            {
                try { p.Kill(); p.WaitForExit(3000); Log("KILL existing " + p.Id); } catch { }
            }
        }

        private static void StartFake()
        {
            if (fake != null && !fake.HasExited) return;
            WriteMode("normal");
            var psi = new ProcessStartInfo
            {
                FileName = fakeExe,
                Arguments = "6060 \"" + modeFile + "\" \"" + Path.Combine(Path.GetTempPath(), "docshot_fakeplc.log") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fakeExe)
            };
            fake = Process.Start(psi);
            Thread.Sleep(1200);
            Log("FAKE started pid=" + fake.Id);
        }

        private static void StopFake()
        {
            if (fake == null) return;
            WriteMode("quit");
            if (!fake.WaitForExit(5000))
            {
                try { fake.Kill(); fake.WaitForExit(2000); } catch { }
            }
            fake = null;
            Thread.Sleep(1200);
            Log("FAKE stopped");
        }

        private static void WriteMode(string mode)
        {
            try { File.WriteAllText(modeFile, mode, Encoding.UTF8); } catch { }
        }

        private static void StartApp()
        {
            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = exeDir,
                UseShellExecute = false
            };
            app = Process.Start(psi);
            Log("APP started pid=" + app.Id);
        }

        private static void WaitMainWindow()
        {
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                try
                {
                    app.Refresh();
                    mainHwnd = app.MainWindowHandle;
                    if (mainHwnd != IntPtr.Zero && !string.IsNullOrEmpty(app.MainWindowTitle)) break;
                }
                catch { }
            }
            if (mainHwnd == IntPtr.Zero) throw new Exception("主窗口未出现");
            Log("MAIN HWND=" + mainHwnd + " TITLE=" + app.MainWindowTitle);
            SetForegroundWindow(mainHwnd);
            Thread.Sleep(1000);
        }

        // ===================== 看门狗 =====================
        private static void Watchdog()
        {
            var t = new Thread(delegate()
            {
                while (watching)
                {
                    try
                    {
                        uint pid = (uint)app.Id;
                        foreach (IntPtr h in TopWindowsOf(pid))
                        {
                            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                            if (cls.ToString() != "#32770" || !IsWindowVisible(h)) continue;
                            lock (knownDialogs) { if (knownDialogs.Contains(h)) continue; }
                            var title = new StringBuilder(256); GetWindowText(h, title, 256);
                            string titleText = title.ToString();
                            // 导出流程自己的两个框由 DoExport 处理，看门狗不许抢：
                            // 实测它会把"导出成功"框点掉，随后 WaitDialog 永远等不到（踩中过）
                            if (titleText.Contains("导出") || titleText.Contains("另存为") || titleText.Contains("Save As")) continue;
                            // 只点掉"意外"模态框：保存/导出框在 DoExport 里自己处理，先登记
                            Log("WATCHDOG dismiss '" + title + "'");
                            IntPtr btn = GetDlgItem(h, 2); // IDCANCEL
                            if (btn != IntPtr.Zero) PostMessage(btn, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                            else PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    catch { }
                    Thread.Sleep(400);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static List<IntPtr> TopWindowsOf(uint pid)
        {
            var list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint p; GetWindowThreadProcessId(h, out p);
                if (p == pid) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ===================== 截图 =====================
        private static void Shot(string file, string note)
        {
            string path = Path.Combine(outDir, file);
            try
            {
                // 【V2.3.0 自愈】06 报警图曾拍到 160×28（主窗 HWND 瞬时失效时
                // GetWindowRect 失败、矩形退化成栈残值）。截图前先确认句柄
                // 有效，失效则从进程重新取主窗句柄再截，避免把垃圾图写进文档。
                if (!IsWindow(mainHwnd))
                {
                    Log("SHOT WARN " + file + " mainHwnd 失效，重新获取主窗句柄");
                    try
                    {
                        var p = Process.GetProcessById(app.Id);
                        var hNew = p.MainWindowHandle;
                        if (hNew != IntPtr.Zero) mainHwnd = hNew;
                    }
                    catch (Exception ex) { Log("SHOT WARN 重新取句柄失败: " + ex.Message); }
                }
                SetForegroundWindow(mainHwnd);
                Thread.Sleep(500);
                RECT r;
                if (!GetWindowRect(mainHwnd, out r))
                {
                    failCount++;
                    Log("SHOT ERROR " + file + " GetWindowRect 失败（mainHwnd=" + mainHwnd + "）");
                    return;
                }
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0)
                {
                    failCount++;
                    Log("SHOT ERROR " + file + " 窗口矩形异常 " + w + "x" + h);
                    return;
                }
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok = PrintWindow(mainHwnd, hdc, 2);
                        g.ReleaseHdc(hdc);
                        if (!ok) Log("PRINTWINDOW FAIL " + file);
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
                Log("SHOT " + file + " " + w + "x" + h + "  " + note + "  " + Metrics(path));
            }
            catch (Exception ex)
            {
                failCount++;
                Log("SHOT ERROR " + file + " " + ex.Message);
            }
        }

        private static string Metrics(string path)
        {
            try
            {
                using (var bmp = new Bitmap(path))
                {
                    var colors = new HashSet<int>();
                    int dark = 0, total = 0;
                    for (int y = 60; y < bmp.Height; y += 7)
                        for (int x = 0; x < bmp.Width; x += 7)
                        {
                            total++;
                            Color c = bmp.GetPixel(x, y);
                            colors.Add((c.R >> 4) << 8 | (c.G >> 4) << 4 | (c.B >> 4));
                            if (c.R + c.G + c.B < 240) dark++;
                        }
                    return "distinct=" + colors.Count + " dark=" + dark + "/" + total;
                }
            }
            catch { return "metrics-error"; }
        }

        // ===================== UIA 操作 =====================
        private static AutomationElement Root()
        {
            return AutomationElement.FromHandle(mainHwnd);
        }

        /// <summary>
        /// 页签文本（显式顺序，与 MainWindow 的 6 个页签一致）。
        /// 【V2.3.0 为什么改成按文本找】页签换成库 KxShiftTab（ToggleButton）后：
        /// UIA 外框 ControlType=Button 但 **Name 为空**（文本在子 TextBlock 上），
        /// 外框实测 231×47、窗口内 y≈95——旧实现按"宽>250、高>50、窗口内 y>120"
        /// 三个坐标条件筛按钮，新样式三个条件全部不匹配（TABS found=0），页签切不动。
        /// 改为先按 TextBlock 的 Name 找文本，再向上找 Button 祖先：不依赖任何
        /// 尺寸/坐标，页签样式再改（更宽/更窄/更高/更低）都不影响。
        /// </summary>
        private static readonly string[] TabTexts =
            { "当天白班产能", "当天夜班产能", "昨天白班产能", "昨天夜班产能", "前天白班产能", "前天夜班产能" };

        private static void ClickTab(int index)
        {
            if (index < 0 || index >= TabTexts.Length) { failCount++; Log("TAB INDEX OUT OF RANGE " + index); return; }
            var root = Root();
            AutomationElement textEl = null;
            for (int retry = 0; retry < 5 && textEl == null; retry++)
            {
                textEl = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, TabTexts[index]));
                if (textEl == null) Thread.Sleep(400);
            }
            if (textEl == null) { failCount++; Log("TAB TEXT MISSING index=" + index + " text=" + TabTexts[index]); return; }
            var tab = GetButtonAncestor(textEl);
            if (tab == null) { failCount++; Log("TAB ANCESTOR MISSING index=" + index); return; }
            // 偶发：第一次点击只激活窗口/点在按钮边缘，页签不切换。
            // 以 ToggleButton 的选中态为准，最多点 3 次（第二次起往左偏一点避开间隙）。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var r = tab.Current.BoundingRectangle;
                Log("TAB rect[" + index + "] " + (int)r.Left + "," + (int)r.Top
                    + " " + (int)r.Width + "x" + (int)r.Height + " attempt=" + attempt);
                ClickCenterRel(tab, "tab" + index, attempt == 0 ? 0.5 : 0.35);
                Thread.Sleep(1200);
                if (IsTabOn(tab)) { Log("TAB OK index=" + index); return; }
            }
            failCount++;
            Log("TAB FAILED index=" + index);
        }

        /// <summary>从元素向上找最近的 ControlType.Button 祖先（页签 ToggleButton 外框）。</summary>
        private static AutomationElement GetButtonAncestor(AutomationElement e)
        {
            var walker = TreeWalker.ControlViewWalker;
            try
            {
                for (var cur = e; cur != null; cur = walker.GetParent(cur))
                {
                    if (cur.Current.ControlType == ControlType.Button) return cur;
                }
            }
            catch { }
            return null;
        }

        /// <summary>ToggleButton 当前是否为选中态（WPF ToggleButton 暴露 TogglePattern）</summary>
        private static bool IsTabOn(AutomationElement e)
        {
            try
            {
                object p;
                if (e.TryGetCurrentPattern(TogglePattern.Pattern, out p))
                    return ((TogglePattern)p).Current.ToggleState == ToggleState.On;
            }
            catch { }
            return false;
        }

        private static AutomationElement FindAid(string aid)
        {
            return Root().FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, aid));
        }

        private static void ClickAid(string aid)
        {
            var e = FindAid(aid);
            if (e == null) { failCount++; Log("AID MISSING " + aid); return; }
            ClickCenter(e, aid);
            Thread.Sleep(1200);
        }

        private static void ClickCenter(AutomationElement e, string what)
        {
            ClickCenterRel(e, what, 0.5);
        }

        private static void ClickCenterRel(AutomationElement e, string what, double relX)
        {
            try
            {
                var r = e.Current.BoundingRectangle;
                int x = (int)(r.Left + r.Width * relX);
                int y = (int)(r.Top + r.Height / 2.0);
                // 置前"该点所在的顶层窗口"，不能一律置前主窗口：
                // 保存对话框打开时若把主窗口置前，鼠标就点到主窗口上，导出流程永远走不完（实测踩中）
                POINT pt; pt.X = x; pt.Y = y;
                IntPtr top = GetAncestor(WindowFromPoint(pt), 2); // GA_ROOT
                SetForegroundWindow(top != IntPtr.Zero ? top : mainHwnd);
                Thread.Sleep(200);
                POINT old; GetCursorPos(out old);
                SetCursorPos(x, y);
                Thread.Sleep(120);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(old.X, old.Y);
                Log("CLICK " + what + " @" + x + "," + y);
            }
            catch (Exception ex)
            {
                failCount++;
                Log("CLICK ERROR " + what + " " + ex.Message);
            }
        }

        private static void InvokeAsync(AutomationElement e, string what)
        {
            var t = new Thread(delegate()
            {
                try
                {
                    object p;
                    if (e.TryGetCurrentPattern(InvokePattern.Pattern, out p))
                        ((InvokePattern)p).Invoke();
                    else
                        Log("NO INVOKE PATTERN " + what);
                }
                catch (Exception ex) { Log("INVOKE ERROR " + what + " " + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
            Log("INVOKE " + what);
        }

        /// <summary>
        /// 等界面上出现至少 min 个文字完全等于 text 的 TextBlock。
        /// 用于等三单元从“运行”变成“空闲”：IDLE 位写入后最多一个刷新周期（1 秒）生效。
        /// </summary>
        private static bool WaitTextCount(string text, int min, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try
                {
                    var all = Root().FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                    int n = 0;
                    foreach (AutomationElement e in all)
                        if (e.Current.Name == text) n++;
                    if (n >= min) { Log("WAIT OK text='" + text + "' count=" + n); return true; }
                }
                catch { }
                Thread.Sleep(400);
                waited += 400;
            }
            failCount++;
            Log("WAIT TIMEOUT text='" + text + "' min=" + min);
            return false;
        }

        /// <summary>
        /// 等指定 AutomationId 的控件文字包含 contains。
        /// 不靠固定 sleep，避免界面刷新慢半拍时拍到前后矛盾的中间态。
        /// </summary>
        private static bool WaitAidText(string aid, string contains, int timeoutMs)
        {
            int waited = 0;
            string last = "";
            while (waited < timeoutMs)
            {
                try
                {
                    var e = FindAid(aid);
                    if (e != null)
                    {
                        last = e.Current.Name ?? "";
                        if (last.Contains(contains)) { Log("WAIT OK " + aid + "='" + last + "'"); return true; }
                    }
                }
                catch { }
                Thread.Sleep(400);
                waited += 400;
            }
            failCount++;
            Log("WAIT TIMEOUT " + aid + " contains='" + contains + "' last='" + last + "'");
            return false;
        }

        /// <summary>
        /// 把标准“另存为”对话框导航到指定目录（避免截到用户“文档”里的真实文件名）。
        /// 做法与资源管理器一致：Alt+D 聚焦地址栏 → Ctrl+A 全选 → 输入路径 → 回车。
        /// 路径必须是 ASCII（本工具用公共文档下的 ProductionHistoryExport，截图里不出现内部目录名）。
        /// </summary>
        /// <remarks>
        /// 【V2.3.0 为什么不用 Alt+D】实测 Win10“另存为”对话框里 Alt+D 不会激活面包屑地址栏，
        /// 路径被打进“文件名”框：旧实现回车后目录确实切过去了，但文件名框残留目录路径，
        /// 紧接着 UIA SetValue 又命中被重建/禁用的元素（ElementNotEnabledException），
        /// 导出截图流程直接中断。改为：点“文件名”框 → Ctrl+A → 输入目录 → 回车
        /// （文件对话框会把带路径的文件名当目录打开），切换后文件名框是干净的。
        /// </remarks>
        private static void NavigateDialog(IntPtr dlg, string dir)
        {
            try
            {
                if (!TypeIntoFileNameBox(dlg, dir, 6000))
                {
                    Log("NAVIGATE ERROR 找不到文件名框");
                    return;
                }
                PressEnter();
                Thread.Sleep(1800);
                Log("NAVIGATE dialog -> " + dir);
            }
            catch (Exception ex) { Log("NAVIGATE ERROR " + ex.Message); }
        }

        /// <summary>
        /// 在“另存为”对话框里找“文件名”编辑框。
        /// 不能只认 AutomationId=1001：实测该 ID 命中的是顶部“搜索”框（把目录文字当成
        /// 搜索词显示在搜索框里），对它 SetValue 会抛 ElementNotEnabledException。
        /// 改按位置筛：位于对话框纵向 60% 之后、宽度超过对话框 30% 的 Edit 即文件名框。
        /// </summary>
        private static AutomationElement FindFileNameEdit(IntPtr dlg, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                try
                {
                    RECT dr; GetWindowRect(dlg, out dr);
                    int dh = dr.Bottom - dr.Top, dw = dr.Right - dr.Left;
                    if (dh > 0 && dw > 0)
                    {
                        var root = AutomationElement.FromHandle(dlg);
                        var edits = root.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        foreach (AutomationElement e in edits)
                        {
                            var b = e.Current.BoundingRectangle;
                            if (b.Height > 10 && b.Width > dw * 0.3 && b.Top > dr.Top + dh * 0.6)
                                return e;
                        }
                    }
                }
                catch { }
                if (waited >= timeoutMs) return null;
                Thread.Sleep(300); waited += 300;
            }
        }

        /// <summary>往“文件名”框输入文本：先点实心（保证焦点），再 Ctrl+A 覆盖、逐字符 Unicode 输入。</summary>
        private static bool TypeIntoFileNameBox(IntPtr dlg, string text, int timeoutMs)
        {
            var edit = FindFileNameEdit(dlg, timeoutMs);
            if (edit == null) return false;
            try
            {
                var r = edit.Current.BoundingRectangle;
                if (r.Width > 4 && r.Height > 4)
                {
                    SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }
                else edit.SetFocus();
            }
            catch { try { edit.SetFocus(); } catch { } }
            Thread.Sleep(250);
            PressCtrlA();
            Thread.Sleep(120);
            foreach (char c in text) SendUnicode(c);
            Thread.Sleep(250);
            return true;
        }

        /// <summary>
        /// 清掉“另存为”对话框顶部“搜索”框里的残留关键字。
        /// 为什么需要：Win10 文件对话框会把上次搜索词跨进程记住，一旦残留，
        /// 文件列表被过滤成“没有与搜索条件匹配的项”，培训文档截图失真。
        /// 做法：优先点搜索框右侧的“清除搜索”按钮（真实点击、Windows 自己刷新列表）；
        /// 找不到按钮才退回 ValuePattern 置空（不按回车，避免误触“保存”）。
        /// </summary>
        private static void ClearDialogSearch(IntPtr dlg)
        {
            try
            {
                RECT dr; GetWindowRect(dlg, out dr);
                int dh = dr.Bottom - dr.Top;
                var root = AutomationElement.FromHandle(dlg);
                var buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement e in buttons)
                {
                    string n = e.Current.Name ?? "";
                    var b = e.Current.BoundingRectangle;
                    if (b.Top < dr.Top + dh * 0.3 && b.Width > 4 &&
                        (n.Contains("清除") || n.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        ClickCenterRel(e, "清除搜索", 0.5);
                        Log("CLEAR search via button '" + n + "'");
                        Thread.Sleep(900);
                        return;
                    }
                }
                var edits = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement e in edits)
                {
                    var b = e.Current.BoundingRectangle;
                    if (b.Top > dr.Top + dh * 0.3) continue;      // 只看顶部搜索框
                    object vp;
                    if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) continue;
                    string val = ((ValuePattern)vp).Current.Value;
                    if (string.IsNullOrEmpty(val)) return;         // 没有残留，什么都不做
                    ((ValuePattern)vp).SetValue("");
                    Log("CLEAR search via SetValue '" + val + "'");
                    Thread.Sleep(600);
                    return;
                }
            }
            catch (Exception ex) { Log("CLEAR SEARCH ERROR " + ex.Message); }
        }

        /// <summary>
        /// 用 UIA ValuePattern 设置“文件名”框文本（只写文件名，不带路径）。
        /// 导航目录后对话框会重建控件，旧引用会变成禁用态，因此这里循环重查：
        /// 每次都重新 FindFileNameEdit，只有 IsEnabled 为真才 SetValue；
        /// 多次仍不可用返回 false，由调用方退回键盘输入。
        /// </summary>
        private static bool SetFileNameByUia(IntPtr dlg, string text)
        {
            for (int i = 0; i < 10; i++)
            {
                var target = FindFileNameEdit(dlg, 0);
                if (target != null)
                {
                    try
                    {
                        if (target.Current.IsEnabled)
                        {
                            object vp;
                            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out vp))
                            {
                                ((ValuePattern)vp).SetValue(text);
                                Log("SET filename via UIA");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex) { Log("SET FILENAME retry: " + ex.Message); }
                }
                Thread.Sleep(300);
            }
            return false;
        }

        private static void PressCtrlA()
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);                       // Ctrl 按下
            keybd_event(0x41, 0, 0, UIntPtr.Zero);                       // A 按下
            keybd_event(0x41, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void PressEnter()
        {
            keybd_event(0x0D, 0, 0, UIntPtr.Zero);
            keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendUnicode(char c)
        {
            var down = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } };
            var up = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
            SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(8);
        }

        // ===================== 导出 =====================
        private static void DoExport()
        {
            WriteMode("normal");
            Thread.Sleep(1500);

            // 导出落点必须是「客户看着正常」的目录：不能用 %TEMP%\docshot_export，
            // 否则截图 09/10 会带出内部工具名和 Administrator 用户名，客户文档不能用。
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "ProductionHistoryExport");
            Directory.CreateDirectory(dir);
            string fileName = "生产履历_" + DateTime.Today.ToString("yyyy-MM-dd") + ".xlsx";
            string xlsx = Path.Combine(dir, fileName);

            // 先清掉同名文件：否则保存对话框会弹"确认另存为"，流程就卡在覆盖确认上（实测踩中）
            string[] stale = new string[]
            {
                xlsx,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName)
            };
            foreach (string f in stale) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

            var exportBtn = FindAid("导出按钮");
            if (exportBtn == null) { failCount++; Log("EXPORT BUTTON MISSING"); return; }
            // 用 UIA Invoke 在后台线程触发：Invoke 会同步等到保存对话框关闭，
            // 放主线程会卡住整个截图流程；放后台线程则主线程可以继续等窗口。
            InvokeAsync(exportBtn, "导出按钮");

            IntPtr dlg = WaitDialog("另存为", 15000);
            if (dlg == IntPtr.Zero) { failCount++; Log("SAVE DIALOG MISSING"); return; }
            lock (knownDialogs) { knownDialogs.Add(dlg); }
            Thread.Sleep(800);
            // 先导航到临时目录再截图：默认落在用户“文档”，会把真实文档名拍进客户文档。
            NavigateDialog(dlg, dir);
            ClearDialogSearch(dlg);
            Thread.Sleep(600);

            // 文件名：导航后该编辑框可能被重建（旧元素禁用/失效），所以统一走
            // “重新查找 + 等可用”的路径；UIA 设置失败再退回键盘输入兜底。
            if (!SetFileNameByUia(dlg, fileName))
            {
                Log("SET filename via UIA 失败，改用键盘输入");
                if (!TypeIntoFileNameBox(dlg, fileName, 4000)) { failCount++; Log("FILENAME EDIT MISSING"); return; }
            }
            Thread.Sleep(300);
            CaptureWindow(dlg, "09-export-save-dialog.png", "导出-保存对话框");

            // 保存按钮用鼠标点（UIA Invoke 会同步等到 MessageBox 关闭，卡住发送线程）
            var dlgRoot = AutomationElement.FromHandle(dlg);
            var buttons = dlgRoot.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            AutomationElement save = null;
            foreach (AutomationElement e in buttons)
            {
                string n = e.Current.Name;
                if (n.Contains("保存") || n.StartsWith("Save")) { save = e; break; }
            }
            if (save == null) { failCount++; Log("SAVE BUTTON MISSING"); return; }
            InvokeAsync(save, "保存");

            IntPtr msg = WaitDialog("导出", 20000);
            if (msg == IntPtr.Zero) { failCount++; Log("EXPORT MESSAGEBOX MISSING"); return; }
            lock (knownDialogs) { knownDialogs.Add(msg); }
            Thread.Sleep(600);
            CaptureWindow(msg, "10-export-success.png", "导出-结果提示");
            string text = ReadDialogText(msg);
            Log("EXPORT RESULT: " + text);
            if (!text.Contains("成功")) failCount++;

            IntPtr ok = GetDlgItem(msg, 1); // IDOK
            if (ok != IntPtr.Zero) PostMessage(ok, 0x00F5, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(600);

            // 保存对话框可能落在"文档"（未指定初始目录）：两个位置都清掉，别把演示文件留在用户目录
            string[] candidates = new string[]
            {
                xlsx,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName)
            };
            foreach (string f in candidates)
            {
                try
                {
                    if (File.Exists(f)) { Log("XLSX SIZE=" + new FileInfo(f).Length + " PATH=" + f); File.Delete(f); }
                }
                catch (Exception ex) { Log("XLSX CLEAN FAIL " + f + " " + ex.Message); }
            }
        }

        private static string ReadDialogText(IntPtr h)
        {
            try
            {
                var root = AutomationElement.FromHandle(h);
                var texts = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                var sb = new StringBuilder();
                foreach (AutomationElement e in texts) sb.Append(e.Current.Name).Append(" ");
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        private static IntPtr WaitDialog(string titlePart, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                foreach (IntPtr h in TopWindowsOf((uint)app.Id))
                {
                    var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                    if (cls.ToString() != "#32770" || !IsWindowVisible(h)) continue;
                    var title = new StringBuilder(256); GetWindowText(h, title, 256);
                    if (string.IsNullOrEmpty(titlePart) || title.ToString().Contains(titlePart)) return h;
                }
                Thread.Sleep(300);
                waited += 300;
            }
            return IntPtr.Zero;
        }

        private static void CaptureWindow(IntPtr hwnd, string file, string note)
        {
            try
            {
                RECT r; GetWindowRect(hwnd, out r);
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(hwnd, hdc, 2);
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save(Path.Combine(outDir, file), ImageFormat.Png);
                }
                Log("SHOT " + file + " " + w + "x" + h + "  " + note);
            }
            catch (Exception ex)
            {
                failCount++;
                Log("CAPTURE ERROR " + file + " " + ex.Message);
            }
        }

        private static void Log(string text)
        {
            lock (logLock)
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text;
                Console.WriteLine(line);
                try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(seedScript), "docshot.log"), line + "\r\n", Encoding.UTF8); } catch { }
            }
        }
    }
}
