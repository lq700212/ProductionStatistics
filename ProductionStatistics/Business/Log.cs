using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 运行日志工具：把软件运行过程写进 exe 目录下的 logs 文件夹，按天分文件（运行日志_yyyy-MM-dd.log）。
    ///
    /// 做什么：
    ///   ① 提供 Info / Warn / Error 三级日志，统一格式「时间 + 级别 + 线程 + 内容」，追加写 UTF-8(带 BOM)。
    ///   ② 启动时自动建 logs 目录，并清理超过保留天数（默认 30 天）的旧日志，防止长期驻留把硬盘写满。
    ///   ③ Error 级同时镜像到 exe 目录的 错误日志.txt（沿用 V1 起的既有约定，现场手册里已写明这个文件）。
    ///
    /// 为什么要有它：现场工控机没人盯屏幕，出了问题只能靠回忆"昨晚到底停没停"。
    ///   有了日志就能把"时间账目"（真实经过多少秒 / 实际计入多少秒 / 差在哪里）落到文件里，
    ///   远程让现场把 logs 文件夹打包发回来就能复盘，不用再靠猜。
    ///
    /// 怎么用：
    ///   Log.Info("软件启动，配置: " + 配置摘要);
    ///   Log.Warn("采样间隔异常 12.3 秒，已丢弃该段");
    ///   Log.Error("保存 CSV 失败", ex);
    ///   Log.Info节流("状态变化", "设备状态: 运行 → 停止", 5);   // 同一类消息 5 秒内只记一条，防刷屏
    ///
    /// 注意：
    ///   - 写日志绝不能把程序搞崩：所有 IO 都在 try-catch 里，失败就放弃这一条（静默降级，不影响统计）。
    ///   - 采集线程、UI 线程、异常处理线程都会写日志，用写锁串行化，避免内容互相写串行。
    ///   - 采集循环里不要每轮都写（每秒好几条会把文件撑爆），只在"状态变化 / 每分钟汇总 / 出错"时写。
    /// </summary>
    public static class Log
    {
        /// <summary>写日志的互斥锁：多线程写同一文件时串行化，防止内容互相穿插</summary>
        private static readonly object 写锁 = new object();

        /// <summary>节流记录表：键 → 上次写入时间（用于 Info节流 的去重）</summary>
        private static readonly Dictionary<string, DateTime> 节流记录 = new Dictionary<string, DateTime>();

        /// <summary>日志保留天数：超过这个天数的日志文件在启动时删掉（现场一般保留一个月足够追溯）</summary>
        public const int 保留天数 = 30;

        /// <summary>日志目录：exe 目录下的 logs 文件夹（现场直接打包这个文件夹发回来即可）</summary>
        public static string LogDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); }
        }

        /// <summary>旧版异常日志文件路径（exe 目录，沿用既有约定，Error 级日志会同时镜像到这里）</summary>
        public static string 错误日志路径
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "错误日志.txt"); }
        }

        /// <summary>某天的日志文件路径：logs\运行日志_yyyy-MM-dd.log（按天分文件，方便按天打包发回）</summary>
        public static string LogFile(DateTime date)
        {
            return Path.Combine(LogDir, "运行日志_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        }

        /// <summary>
        /// 日志初始化：建 logs 目录 + 清理过期日志文件 + 写一行启动分隔线。
        /// 在程序最开始（Program.Main）调用一次即可；任何一步失败都只是少了个日志文件，不影响程序运行。
        /// </summary>
        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                清理过期日志();
                Write("INFO", "----- 日志开始（本行仅作分隔，程序每次启动都会写一条） -----", null);
            }
            catch
            {
                // 建目录/清理失败都不该影响程序运行，忽略即可
            }
        }

        /// <summary>普通信息（正常运行过程的记录）</summary>
        public static void Info(string 内容)
        {
            Write("INFO", 内容, null);
        }

        /// <summary>警告（能继续跑，但有异常情况需要留意，例如采样间隔过大、离线次数偏多）</summary>
        public static void Warn(string 内容)
        {
            Write("WARN", 内容, null);
        }

        /// <summary>错误（功能受影响，例如保存 CSV 失败、读取 PLC 持续失败）</summary>
        public static void Error(string 内容)
        {
            Write("ERROR", 内容, null);
        }

        /// <summary>错误 + 异常对象（把异常类型、消息、堆栈一起落盘，排查时不用再复现一次）</summary>
        public static void Error(string 内容, Exception ex)
        {
            Write("ERROR", 内容, ex == null ? "" : " | " + ex.Message + " | " + ex.StackTrace);
        }

        /// <summary>
        /// 节流写入：同一个"键"的内容在最小间隔秒内只写第一条。
        /// 用途：设备状态来回抖动时（如 IDLE 信号频繁翻转）避免日志被刷爆，
        ///   关键状态变化仍然看得见，抖动细节由每分钟的汇总行体现。
        /// </summary>
        public static void Info节流(string 键, string 内容, int 最小间隔秒)
        {
            节流("INFO", 键, 内容, 最小间隔秒);
        }

        /// <summary>节流写入（WARN 级）：同一类告警在最小间隔秒内只记第一条，避免刷屏</summary>
        public static void Warn节流(string 键, string 内容, int 最小间隔秒)
        {
            节流("WARN", 键, 内容, 最小间隔秒);
        }

        /// <summary>
        /// 节流写入的通用实现：先看这个"键"最近是否记过（小于最小间隔秒就跳过），再按指定级别落盘。
        /// 键一般用"事件类型+对象"拼成，如 "脉冲过窄:焊接OK(M3210)"，这样不同对象的告警互不影响。
        /// </summary>
        private static void 节流(string 级别, string 键, string 内容, int 最小间隔秒)
        {
            lock (写锁)
            {
                DateTime 上次;
                if (节流记录.TryGetValue(键, out 上次) && (DateTime.Now - 上次).TotalSeconds < 最小间隔秒)
                    return; // 距离上次太近，这条丢掉
                节流记录[键] = DateTime.Now;
            }
            Write(级别, 内容, null);
        }

        /// <summary>
        /// 真正落盘：加锁 → 追加写当天日志文件 → Error 级再镜像一份到 错误日志.txt。
        /// 整个方法体都在 try-catch 里：写日志失败只放弃这一条日志，绝不让主流程因为"记个日志"而崩掉。
        /// </summary>
        private static void Write(string 级别, string 内容, string 附加)
        {
            try
            {
                DateTime 现在 = DateTime.Now;
                string 线程名 = Thread_当前名称();
                string 行 = 现在.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                          + " [" + 级别.PadRight(5) + "] [" + 线程名 + "] " + 内容;
                if (!string.IsNullOrEmpty(附加)) 行 += " " + 附加;

                lock (写锁)
                {
                    Directory.CreateDirectory(LogDir);
                    // 追加写：UTF-8 带 BOM（现场用记事本/Excel 打开不乱码）
                    File.AppendAllText(LogFile(现在), 行 + Environment.NewLine, new UTF8Encoding(true));
                    // Error 级同时写进旧版错误日志.txt，保持现场手册里"错误日志.txt"的既有约定可用
                    if (级别 == "ERROR")
                        File.AppendAllText(错误日志路径, 行 + Environment.NewLine, new UTF8Encoding(true));
                }
            }
            catch
            {
                // 兜底：日志写不了就算了，绝不能因此影响统计主流程
            }
        }

        /// <summary>取当前线程名（没命名就用"主线程"兜底，日志里一眼能看出是谁写的）</summary>
        private static string Thread_当前名称()
        {
            try
            {
                string 名字 = System.Threading.Thread.CurrentThread.Name;
                return string.IsNullOrEmpty(名字) ? "主线程" : 名字;
            }
            catch
            {
                return "未知线程";
            }
        }

        /// <summary>
        /// 清理过期日志文件：删掉 logs 目录下文件名日期早于"今天 - 保留天数"的 .log 文件。
        /// 只认本工具自己生成的命名格式（运行日志_yyyy-MM-dd.log），不会误删现场放进去的其它文件。
        /// </summary>
        private static void 清理过期日志()
        {
            try
            {
                if (!Directory.Exists(LogDir)) return;
                DateTime 截止 = DateTime.Today.AddDays(-保留天数);
                foreach (string 文件 in Directory.GetFiles(LogDir, "运行日志_*.log"))
                {
                    string 名 = Path.GetFileNameWithoutExtension(文件);   // 运行日志_yyyy-MM-dd
                    string 日期段 = 名.Substring("运行日志_".Length);
                    DateTime 文件日期;
                    if (DateTime.TryParseExact(日期段, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out 文件日期)
                        && 文件日期 < 截止)
                    {
                        File.Delete(文件);
                    }
                }
            }
            catch
            {
                // 清理失败无所谓，旧文件留着只是占点硬盘
            }
        }
    }
}
