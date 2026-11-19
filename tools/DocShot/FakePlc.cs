using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using HslCommunication.Profinet.Melsec;

namespace DocShot
{
    /// <summary>
    /// 假 PLC（三菱 MC 3E 二进制服务器），供文档截图时驱动真实软件的采集状态机。
    ///
    /// 为什么需要：生产统计软件没有内置 Mock，直接把 PLC 连不上/空数据拍进文档不合格。
    /// 这里按真实 MC 协议对外提供 M3200~M3232 点位，软件照常连接、照常上升沿计数、照常落盘，
    /// 属于"确定性仿真源"，不是 P 图。
    ///
    /// 用法：FakePlc.exe [端口] [模式文件] [日志文件]
    ///   模式文件内容（运行中可改，1 秒内生效）：
    ///     normal —— 设备自动中，三单元轮流产生 OK 脉冲（每 12 件夹 1 件 NG）
    ///     alarm  —— 设备报警，不产生脉冲
    ///     idle   —— 单元待料（IDLE 全开）
    ///     stop   —— 设备停止（自动中/报警/IDLE 全关）
    /// 缺省：6060，%TEMP%\docshot_mode.txt，%TEMP%\docshot_fakeplc.log
    /// </summary>
    internal static class FakePlc
    {
        private static MelsecMcServer server;
        private static string modeFile;
        private static string logFile;
        private static volatile bool running = true;

        private static int Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 6060;
            modeFile = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "docshot_mode.txt");
            logFile = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "docshot_fakeplc.log");

            server = new MelsecMcServer(true);
            server.ServerStart(port);
            Log("SERVER START port=" + port);

            // 初始点位：自动中 ON，其余 OFF
            Write("M3200", true);
            Write("M3201", false);
            for (int i = 0; i < 3; i++)
            {
                Write(OkAddr(i), false);
                Write(NgAddr(i), false);
                Write(IdleAddr(i), false);
            }

            string lastMode = null;
            DateTime lastPulse = DateTime.MinValue;
            int pulseIndex = 0;
            int pieceIndex = 0;
            DateTime lastModeRead = DateTime.MinValue;

            while (running)
            {
                try
                {
                    if ((DateTime.Now - lastModeRead).TotalMilliseconds >= 500)
                    {
                        lastModeRead = DateTime.Now;
                        string mode = ReadMode();
                        if (mode == "quit") { Log("MODE=quit"); break; }
                        if (mode != lastMode)
                        {
                            lastMode = mode;
                            ApplyMode(mode);
                            Log("MODE=" + mode);
                        }
                    }

                    if (lastMode == "normal" && (DateTime.Now - lastPulse).TotalSeconds >= 1.8)
                    {
                        lastPulse = DateTime.Now;
                        // 轮流三个单元，每 12 件夹 1 件 NG
                        int unit = pulseIndex % 3;
                        bool ng = (pieceIndex % 12) == 11;
                        string addr = ng ? NgAddr(unit) : OkAddr(unit);
                        Write(addr, true);
                        Log("PULSE " + (ng ? "NG" : "OK") + " " + addr);
                        pulseIndex++;
                        pieceIndex++;
                    }
                }
                catch (Exception ex)
                {
                    Log("ERROR " + ex.Message);
                }
                Thread.Sleep(120);
            }

            server.ServerClose();
            Log("SERVER STOP");
            return 0;
        }

        private static string ReadMode()
        {
            try
            {
                if (File.Exists(modeFile))
                {
                    string text = File.ReadAllText(modeFile, Encoding.UTF8).Trim().ToLowerInvariant();
                    if (text == "normal" || text == "alarm" || text == "idle" || text == "stop" || text == "quit") return text;
                }
            }
            catch { }
            return "normal";
        }

        private static void ApplyMode(string mode)
        {
            if (mode == "normal")
            {
                Write("M3200", true);
                Write("M3201", false);
                for (int i = 0; i < 3; i++) Write(IdleAddr(i), false);
            }
            else if (mode == "alarm")
            {
                Write("M3200", false);
                Write("M3201", true);
                for (int i = 0; i < 3; i++) Write(IdleAddr(i), false);
            }
            else if (mode == "idle")
            {
                Write("M3200", false);
                Write("M3201", false);
                for (int i = 0; i < 3; i++) Write(IdleAddr(i), true);
            }
            else
            {
                Write("M3200", false);
                Write("M3201", false);
                for (int i = 0; i < 3; i++) Write(IdleAddr(i), false);
            }
        }

        private static string OkAddr(int unit) { return "M32" + (1 + unit) + "0"; }
        private static string NgAddr(int unit) { return "M32" + (1 + unit) + "1"; }
        private static string IdleAddr(int unit) { return "M32" + (1 + unit) + "2"; }

        private static void Write(string address, bool value)
        {
            var r = server.Write(address, new[] { value });
            if (!r.IsSuccess) Log("WRITE FAIL " + address + " " + r.Message);
        }

        private static void Log(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text;
            Console.WriteLine(line);
            try { File.AppendAllText(logFile, line + "\r\n", Encoding.UTF8); } catch { }
        }
    }
}
