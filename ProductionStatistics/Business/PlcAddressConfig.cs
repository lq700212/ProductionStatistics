using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// PLC 信号地址配置：把《PLC地址定义.txt》里的每一个点位都做成"生产统计.ini"里可改的配置项，
    /// 现场换设备/改地址时只改 ini，不用重新编译软件。
    ///
    /// ini 里的写法（缺省生成时也会自动写进去）：
    /// <code>
    /// [PLC地址]
    /// 设备自动中=M3200
    /// 设备报警=M3201
    /// 焊接OK=M3210
    /// 焊接NG=M3211
    /// 焊接IDLE=M3212
    /// 气密OK=M3220
    /// 气密NG=M3221
    /// 气密IDLE=M3222
    /// 贴标OK=M3230
    /// 贴标NG=M3231
    /// 贴标IDLE=M3232
    /// </code>
    ///
    /// 兼容性：老版本的 ini 里没有 [PLC地址] 段，照样能跑——读不到的键一律走缺省值（即《PLC地址定义.txt》里的原值）。
    ///
    /// 读取策略：同一个单元的 OK/NG/IDLE 三个地址**数值上连续**时（例：M3210/M3211/M3212），
    ///   一次报文 ReadBool(起始, 3) 读完（省报文，现场 PLC 负担小）；
    ///   不连续时（例：M3210/M3015/M3220）就分三次单点读，保证现场可以自由配地址。
    ///
    /// 本文件被 WinForms 与 WPF 两个工程**链接引用同一份源文件**（csproj 的 Compile Include + Link），
    ///   所以两边的地址口径永远一致，不会出现"改了一边忘了另一边"。
    /// </summary>
    public class PlcAddressConfig
    {
        /// <summary>设备自动中地址（M3200）</summary>
        public string 设备自动中 = "M3200";

        /// <summary>设备报警地址（M3201）</summary>
        public string 设备报警 = "M3201";

        /// <summary>三个单元的 OK 计数触发地址（下标 0=焊接 1=气密 2=贴标）</summary>
        public string[] 单元OK = { "M3210", "M3220", "M3230" };

        /// <summary>三个单元的 NG 计数触发地址</summary>
        public string[] 单元NG = { "M3211", "M3221", "M3231" };

        /// <summary>三个单元的 IDLE 地址</summary>
        public string[] 单元IDLE = { "M3212", "M3222", "M3232" };

        /// <summary>
        /// 不复位的地址（ini 里逗号分隔，可选）：这些地址读到 true 后软件**不会**写回 false。
        /// 默认只有 设备自动中 / 设备报警 不复位（它们是状态信号，保持现状）。
        /// 若某个信号其实是 PLC 持续维护的状态（复位后 PLC 立刻又置 true），
        /// 在这里排除它，可以少写一次 PLC（省通讯负担）。例如：不复位地址=M3212,M3232
        /// </summary>
        public string 不复位地址 = "";

        /// <summary>
        /// 从 ini 读取地址配置。
        /// 读不到的键（老 ini 没有 [PLC地址] 段，或某个键被删了）一律用缺省值，绝不因为配置缺失而起不来。
        /// </summary>
        public static PlcAddressConfig 从Ini(Dictionary<string, string> ini)
        {
            var 配置 = new PlcAddressConfig();
            if (ini == null) return 配置;

            配置.设备自动中 = 取(ini, "PLC地址", "设备自动中", 配置.设备自动中);
            配置.设备报警 = 取(ini, "PLC地址", "设备报警", 配置.设备报警);
            配置.不复位地址 = 取(ini, "PLC地址", "不复位地址", 配置.不复位地址);

            string[] 单元名 = { "焊接", "气密", "贴标" };
            for (int i = 0; i < 3; i++)
            {
                配置.单元OK[i] = 取(ini, "PLC地址", 单元名[i] + "OK", 配置.单元OK[i]);
                配置.单元NG[i] = 取(ini, "PLC地址", 单元名[i] + "NG", 配置.单元NG[i]);
                配置.单元IDLE[i] = 取(ini, "PLC地址", 单元名[i] + "IDLE", 配置.单元IDLE[i]);
            }
            return 配置;
        }

        /// <summary>取 ini 里的字符串（值为空时用缺省值）</summary>
        private static string 取(Dictionary<string, string> ini, string 节, string 键, string 缺省值)
        {
            string 值;
            if (ini.TryGetValue(节 + "." + 键, out 值) && 值 != null && 值.Trim().Length > 0)
                return 值.Trim().ToUpperInvariant(); // 地址统一大写（M3210 / m3010 都认）
            return 缺省值;
        }

        /// <summary>缺省配置文本（首次生成 ini 时写进 [PLC地址] 段，现场照着改即可）</summary>
        public static string 缺省配置文本()
        {
            var sb = new StringBuilder();
            sb.Append("[PLC地址]\r\n");
            sb.Append("设备自动中=M3200\r\n");
            sb.Append("设备报警=M3201\r\n");
            sb.Append("焊接OK=M3210\r\n");
            sb.Append("焊接NG=M3211\r\n");
            sb.Append("焊接IDLE=M3212\r\n");
            sb.Append("气密OK=M3220\r\n");
            sb.Append("气密NG=M3221\r\n");
            sb.Append("气密IDLE=M3222\r\n");
            sb.Append("贴标OK=M3230\r\n");
            sb.Append("贴标NG=M3231\r\n");
            sb.Append("贴标IDLE=M3232\r\n");
            sb.Append("# 不复位地址（可选，逗号分隔）：这些地址读到 true 后软件不会写回 false。\r\n");
            sb.Append("# 默认只有 设备自动中 / 设备报警 不复位（它们是状态信号）；\r\n");
            sb.Append("# 若某个信号是 PLC 持续状态（复位后立刻又变 true），在这里排除它可少写一次 PLC。\r\n");
            sb.Append("# 不复位地址=\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// 某个单元的 OK/NG/IDLE 三个地址是否数值连续。
        /// 连续 → 采集时一次 ReadBool(起始,3) 读完；不连续 → 分三次单点读。
        /// </summary>
        public bool 单元地址连续(int 单元号)
        {
            int ok = 解析地址(单元OK[单元号]);
            int ng = 解析地址(单元NG[单元号]);
            int idle = 解析地址(单元IDLE[单元号]);
            if (ok < 0 || ng < 0 || idle < 0) return false;
            return (ng == ok + 1) && (idle == ng + 1);
        }

        /// <summary>设备自动中与设备报警两个地址是否数值连续（连续时可一次读两点）</summary>
        public bool 自动中报警连续()
        {
            int 自动 = 解析地址(设备自动中);
            int 报警 = 解析地址(设备报警);
            if (自动 < 0 || 报警 < 0) return false;
            return 报警 == 自动 + 1;
        }

        // ===================== V2.2.2 全信号合并读（一次报文读回 11 个位） =====================
        //
        // 【为什么要有合并读】老写法每轮 4 次报文，单轮采样周期实测 200~330ms；
        // 而"自锁位复位 → PLC 重新置位下一件"的空档实测约 230ms——空档宽度与采样周期几乎相等，
        // 去抖的"确认过 OFF"判据（连续 2 轮 OFF ≈ 460ms）经常把**真实的下一件**也拦掉（漏计），
        // 这就是现场"总数跟单站对不上"的机制根源。
        // 11 个位合并成 1 次报文后，单轮周期降到 ~120ms，空档（230ms）能被稳定看到 ≥1.5 个采样周期，
        // 配合去抖改成"按 OFF 持续时长确认"（见 TriggerDebouncer），漏计窗口被压到接近于零。

        /// <summary>合并读窗口上限（点）：11 个地址的数值跨度不超过它才合并，超出回落 4 次报文老路径</summary>
        public const int 合并读窗口上限 = 100;

        /// <summary>
        /// 11 个信号地址是否可合并成一次报文读取。
        /// 条件：①全部地址都是同一前缀字母（M/X/Y…，MC 协议按区读，混区不能一次读）；
        ///      ②数值跨度 ≤ <see cref="合并读窗口上限"/>（现场 M3200~M3232 跨度 33，绰绰有余）。
        /// </summary>
        public bool 可一次合并读()
        {
            char 前缀;
            int 最小, 最大;
            return 全部地址跨度(out 前缀, out 最小, out 最大);
        }

        /// <summary>合并读的起始地址文本（如 "M3200"）。调用前必须先确认 可一次合并读()==true。</summary>
        public string 合并读起始地址()
        {
            char 前缀; int 最小, 最大;
            全部地址跨度(out 前缀, out 最小, out 最大);
            return 前缀 + 最小.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>合并读的点数（最大地址 - 最小地址 + 1）。调用前必须先确认 可一次合并读()==true。</summary>
        public ushort 合并读数量()
        {
            char 前缀; int 最小, 最大;
            全部地址跨度(out 前缀, out 最小, out 最大);
            return (ushort)(最大 - 最小 + 1);
        }

        /// <summary>
        /// 合并读偏移表：11 个地址相对合并读起始地址的偏移（下标即 ReadBool 返回数组里的位置）。
        /// 顺序固定为：0=设备自动中 1=设备报警，之后每单元 3 个（OK/NG/IDLE）——
        /// 2~4=焊接 5~7=气密 8~10=贴标。调用前必须先确认 可一次合并读()==true。
        /// </summary>
        public int[] 合并读偏移表()
        {
            char 前缀; int 最小, 最大;
            全部地址跨度(out 前缀, out 最小, out 最大);
            string[] 全部 = 全部地址数组();
            var 偏移 = new int[全部.Length];
            for (int i = 0; i < 全部.Length; i++)
            {
                int 数值;
                尝试解析地址(全部[i], out 前缀, out 数值);
                偏移[i] = 数值 - 最小;
            }
            return 偏移;
        }

        /// <summary>11 个信号地址按固定顺序排成一维数组：0=自动中 1=报警，之后每单元 OK/NG/IDLE</summary>
        private string[] 全部地址数组()
        {
            var arr = new string[2 + 3 * 3];
            arr[0] = 设备自动中;
            arr[1] = 设备报警;
            for (int i = 0; i < 3; i++)
            {
                arr[2 + 3 * i] = 单元OK[i];
                arr[3 + 3 * i] = 单元NG[i];
                arr[4 + 3 * i] = 单元IDLE[i];
            }
            return arr;
        }

        /// <summary>
        /// 求 11 个地址的共同前缀与数值跨度。
        /// 返回 false = 有地址解析不了 / 前缀不一致 / 跨度超窗口（调用方回落逐点读）。
        /// </summary>
        private bool 全部地址跨度(out char 前缀, out int 最小, out int 最大)
        {
            前缀 = '\0'; 最小 = int.MaxValue; 最大 = int.MinValue;
            string[] 全部 = 全部地址数组();
            for (int i = 0; i < 全部.Length; i++)
            {
                char p; int 数值;
                if (!尝试解析地址(全部[i], out p, out 数值)) return false;
                if (前缀 == '\0') 前缀 = p;
                else if (前缀 != p) return false;      // 混区（如 M 与 X 混用）不能一次读
                if (数值 < 最小) 最小 = 数值;
                if (数值 > 最大) 最大 = 数值;
            }
            if (前缀 == '\0') return false;
            return (最大 - 最小 + 1) <= 合并读窗口上限;
        }

        /// <summary>
        /// 把地址解析成"前缀字母 + 数值"（"M3210" → 'M' + 3210）。
        /// 解析不了（空串/无数字）返回 false。
        /// </summary>
        public static bool 尝试解析地址(string 地址, out char 前缀, out int 数值)
        {
            前缀 = '\0'; 数值 = -1;
            if (string.IsNullOrEmpty(地址)) return false;
            var sb = new StringBuilder();
            foreach (char c in 地址)
                if (c >= '0' && c <= '9') sb.Append(c);
            if (sb.Length == 0) return false;
            if (!int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out 数值)) return false;
            // 前缀 = 地址里第一个非数字字符（M/X/Y/D/B…）；纯数字地址前缀给 '\0' 也能合并（同为一区）
            foreach (char c in 地址)
            {
                if (c < '0' || c > '9') { 前缀 = c; break; }
            }
            return true;
        }

        /// <summary>
        /// 把地址解析成数值（"M3210" → 3010），只认地址里的数字部分（M/X/Y/D 都按数字处理）。
        /// 解析不了返回 -1（调用方据此判定"不能合并读"）。
        /// </summary>
        public static int 解析地址(string 地址)
        {
            if (string.IsNullOrEmpty(地址)) return -1;
            var sb = new StringBuilder();
            foreach (char c in 地址)
                if (c >= '0' && c <= '9') sb.Append(c);
            int 值;
            if (sb.Length == 0) return -1;
            if (!int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out 值)) return -1;
            return 值;
        }

        /// <summary>日志用的一行摘要（启动时打印，便于现场确认软件到底在读哪些地址）</summary>
        public string 摘要()
        {
            var sb = new StringBuilder();
            sb.Append("自动中=").Append(设备自动中).Append(" 报警=").Append(设备报警);
            string[] 单元名 = { "焊接", "气密", "贴标" };
            for (int i = 0; i < 3; i++)
            {
                sb.Append(" | ").Append(单元名[i]).Append("=")
                  .Append(单元OK[i]).Append("/").Append(单元NG[i]).Append("/").Append(单元IDLE[i]);
            }
            return sb.ToString();
        }
    }
}
