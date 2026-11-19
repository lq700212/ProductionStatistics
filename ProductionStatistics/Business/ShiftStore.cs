using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 小时统计桶：一个班次里某一个整点小时的所有统计数据。
    /// 做什么：存放"某天某班 08:00-09:00"这类一个小时的 OK/NG 计数（分单元）与四类时间累计（秒）。
    /// 为什么分单元存：界面主表只显示合计，但右侧单元状态栏和 Excel 导出要分单元明细，
    ///   现场追查"哪个单元 NG 多"时不用回 PLC 翻记录。
    /// 怎么改：要加新统计列（比如手动态时间），这里加字段 + CSV 表头加列 + RowText/LoadCsv 同步改。
    /// </summary>
    public class HourStat
    {
        /// <summary>三个单元的 OK 计数，下标 0=焊接 1=气密 2=贴标（与 Form1.单元名称 一一对应）</summary>
        public int[] UnitOk = new int[3];

        /// <summary>三个单元的 NG 计数，下标含义同 UnitOk</summary>
        public int[] UnitNg = new int[3];

        /// <summary>四类时间累计（单位：秒）。生产=自动中运行；待料=非自动且单元 IDLE；报警/停止见 Form1 统计口径</summary>
        public double ProductionSec, WaitSec, AlarmSec, StopSec;

        /// <summary>合计 OK（三单元之和）</summary>
        public int OkCount { get { return UnitOk[0] + UnitOk[1] + UnitOk[2]; } }

        /// <summary>合计 NG（三单元之和）</summary>
        public int NgCount { get { return UnitNg[0] + UnitNg[1] + UnitNg[2]; } }

        /// <summary>小时产能 = OK + NG（与现场 HMI"小时产能(PCS)"口径一致）</summary>
        public int TotalCount { get { return OkCount + NgCount; } }

        /// <summary>某单元 OK 计数 +1（上升沿触发时由采集线程调用）</summary>
        public void AddOk(int unitIndex) { UnitOk[unitIndex]++; }

        /// <summary>某单元 NG 计数 +1（上升沿触发时由采集线程调用）</summary>
        public void AddNg(int unitIndex) { UnitNg[unitIndex]++; }
    }

    /// <summary>
    /// 一天的统计记录：白班 12 个小时桶 + 夜班 12 个小时桶。
    /// 夜班（20:00-次日08:00）的归属日期 = 20:00 所在的那一天，所以一天正好装满两个班次。
    /// </summary>
    public class DayRecord
    {
        /// <summary>记录归属日期（夜班按 20:00 所在日期归档）</summary>
        public DateTime Date;

        /// <summary>白班 12 个小时桶，下标 0 对应 08:00-09:00</summary>
        public HourStat[] DayShift = NewShift();

        /// <summary>夜班 12 个小时桶，下标 0 对应 20:00-21:00（跨天后 00:00 起对应下标 4）</summary>
        public HourStat[] NightShift = NewShift();

        private static HourStat[] NewShift()
        {
            var arr = new HourStat[12];
            for (int i = 0; i < 12; i++) arr[i] = new HourStat();
            return arr;
        }
    }

    /// <summary>
    /// 统计数据的按日 CSV 存取工具（【V2.3.1】起 CSV 不再是主存储）。
    /// 现在的职责只剩两件事：
    ///   ① Load：旧版升级时把历史 CSV 一次性导入 SQLite 库（见 StatisticsDbStore.导入历史Csv）；
    ///   ② DataDir/CsvPath：数据目录与文件命名口径（历史查询页的 CSV 导出沿用"数据"目录）。
    /// 主存储是 数据\生产统计.db（SQLite），CSV 的实时写入已移除（历史查询导出走 CSV 文本生成）。
    /// 文件格式（UTF-8 带 BOM，Excel 双击打开不乱码）：表头 1 行 + 白班 12 行 + 夜班 12 行，固定行序，
    ///   解析时严格按行序对应班次/小时，不靠文字匹配，避免现场手改文件文字后错位。
    /// 注意：读写数字一律用 InvariantCulture（小数点固定"."）——Windows 区域语言若设成小数逗号的国家，
    ///   按 CurrentCulture 会把"60.0"读错/写成"60,0"，而逗号正是 CSV 分隔符，会把整份文件搞乱（V2.0.0 修复）。
    /// </summary>
    public static class ShiftStore
    {
        /// <summary>每个班次的小时行数（固定 12 个整点行）</summary>
        public const int RowsPerShift = 12;

        /// <summary>CSV 表头（列序即 RowText 写出顺序，改这里必须同步改 RowText 与 Load）</summary>
        public static readonly string[] 列标题 =
        {
            "时段", "小时产能(PCS)", "OK产能(PCS)", "NG产能(PCS)",
            "焊接OK", "焊接NG", "气密OK", "气密NG", "贴标OK", "贴标NG",
            "生产时间(Min)", "待料时间(Min)", "报警时间(Min)", "停止时间(Min)"
        };

        /// <summary>数据落盘目录：exe 目录下的"数据"文件夹</summary>
        public static string DataDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "数据"); }
        }

        /// <summary>某天的 CSV 文件路径</summary>
        public static string CsvPath(DateTime date)
        {
            return Path.Combine(DataDir, "生产履历_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
        }

        /// <summary>
        /// 生成某班次 12 个整点行的"时段"文字，如白班 ["08:00-09:00"..."19:00-20:00"]，
        /// 夜班 ["20:00-21:00"..."23:00-00:00","00:00-01:00"..."07:00-08:00"]。
        /// 界面行标题与 CSV 行文字都用它生成，保证两处一致。
        /// </summary>
        public static string[] ShiftTitles(bool isDayShift)
        {
            var titles = new string[RowsPerShift];
            for (int i = 0; i < RowsPerShift; i++)
            {
                int startHour = (isDayShift ? 8 : 20) + i;
                int endHour = startHour + 1;
                titles[i] = (startHour % 24).ToString("00") + ":00-" + (endHour % 24).ToString("00") + ":00";
            }
            return titles;
        }

    /// <summary>
    /// 读某天的 CSV；文件不存在返回 null（正常情况，比如刚装的机器），内容坏行直接跳过。
        /// 严格按行序还原：第 1~12 行白班、第 13~24 行夜班（不含表头）。
        /// </summary>
        public static DayRecord Load(DateTime date)
        {
            string path = CsvPath(date);
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            var rec = new DayRecord { Date = date };
            // 跳过表头，逐行按列序解析；任何一行坏掉只丢那一行，不影响其余数据
            for (int row = 1; row < lines.Length && row <= 1 + RowsPerShift * 2; row++)
            {
                string[] cols = lines[row].Split(',');
                if (cols.Length < 列标题.Length) continue;
                bool isDayShift = row <= RowsPerShift;
                int hourIndex = isDayShift ? (row - 1) : (row - 1 - RowsPerShift);
                HourStat s = isDayShift ? rec.DayShift[hourIndex] : rec.NightShift[hourIndex];
                try
                {
                    // 列序与 列标题/RowText 一一对应；列 1~2 是合计（由分单元算出），读文件时以分单元列为准，不重复累加
                    // 数字一律 InvariantCulture 解析，防止区域语言差异（小数逗号国家）把"60.0"读错
                    s.UnitOk[0] = int.Parse(cols[4], CultureInfo.InvariantCulture);  s.UnitNg[0] = int.Parse(cols[5], CultureInfo.InvariantCulture);
                    s.UnitOk[1] = int.Parse(cols[6], CultureInfo.InvariantCulture);  s.UnitNg[1] = int.Parse(cols[7], CultureInfo.InvariantCulture);
                    s.UnitOk[2] = int.Parse(cols[8], CultureInfo.InvariantCulture);  s.UnitNg[2] = int.Parse(cols[9], CultureInfo.InvariantCulture);
                    s.ProductionSec = double.Parse(cols[10], CultureInfo.InvariantCulture) * 60.0;
                    s.WaitSec       = double.Parse(cols[11], CultureInfo.InvariantCulture) * 60.0;
                    s.AlarmSec      = double.Parse(cols[12], CultureInfo.InvariantCulture) * 60.0;
                    s.StopSec       = double.Parse(cols[13], CultureInfo.InvariantCulture) * 60.0;
                }
                catch
                {
                    // 坏行（如被现场编辑过）跳过，保留该行已解析的部分，程序继续跑
                }
            }
            return rec;
        }
    }
}
