using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 统计数据仓库：内存里的"日期 × 班次 × 小时"统计结构 + 按日 CSV 落盘。
    ///
    /// 做什么（与 WinForms 版完全一致，代码从 Form1 搬过来，逻辑没改）：
    ///   ① 启动时把 今天/昨天/前天 的 CSV 读回内存，实现跨重启续算；
    ///   ② 每 30 秒把内存里的记录整体写成 CSV（整文件覆盖，避免半行脏数据）；
    ///   ③ 对外提供"取某天的记录"（已加锁）与"锁内拼 CSV 文本"，锁内不做 IO。
    ///
    /// 线程约定：采集线程写、UI 线程读，落盘也读 —— 统一过 锁。
    ///   锁内只做内存操作；写文件一律出锁后再做（见 保存全部）。
    /// </summary>
    public class StatisticsStore
    {
        /// <summary>数据互斥锁：采集线程写、UI 线程读、落盘读，统一过锁</summary>
        public readonly object 锁 = new object();

        /// <summary>内存里的统计数据，按日期索引（夜班归属 20:00 所在日期）</summary>
        public Dictionary<DateTime, DayRecord> dayRecords = new Dictionary<DateTime, DayRecord>();

        /// <summary>上次落盘时刻，控制"每 30 秒存一次 CSV"</summary>
        public DateTime lastSaveTime = DateTime.MinValue;

        /// <summary>上次成功落盘并写日志的时刻：落盘每 30 秒一次，全部写日志会刷屏，按这个节流</summary>
        public DateTime lastSaveLogTime = DateTime.MinValue;

        /// <summary>启动时把 最近三天 的 CSV 读回内存，实现跨重启续算（文件不存在就建空白记录，不报错）</summary>
        public void 载入最近三天()
        {
            StringBuilder 概况 = new StringBuilder();
            lock (锁)
            {
                for (int i = 0; i < 3; i++)
                {
                    DateTime date = DateTime.Today.AddDays(-i);
                    DayRecord rec = ShiftStore.Load(date);
                    bool 无历史 = (rec == null); // CSV 不存在 = 这天从零开始记（不是续算）
                    if (rec == null) rec = new DayRecord { Date = date };
                    dayRecords[date] = rec;
                    概况.Append(date.ToString("yyyy-MM-dd")).Append(无历史 ? "(无历史,新建)" : "(已续算)").Append("  ");
                }
            }
            Log.Info("载入历史数据(最近三天): " + 概况.ToString());
        }

        /// <summary>
        /// 取某天的记录（没有就建空白）；调用方需持有 锁（或处于 UI 单线程上下文）。
        /// </summary>
        public DayRecord 取记录(DateTime date)
        {
            DayRecord rec;
            if (!dayRecords.TryGetValue(date, out rec))
            {
                rec = new DayRecord { Date = date };
                dayRecords[date] = rec;
            }
            return rec;
        }

        /// <summary>
        /// 把内存里最近 7 天全部写成 CSV（整文件覆盖写）；失败原因记入 fileError 供标题栏提示。
        /// 内存只保留最近 7 天（页签只用 3 天，留 4 天余量），防止长期驻留后字典无限增长。
        /// </summary>
        public string 保存全部()
        {
            try
            {
                // 锁内只拷快照（直接生成整个 CSV 文本，数据量极小 25 行，串行化足够快，无长期 IO 竞争）
                List<KeyValuePair<DateTime, string>> snapshots = new List<KeyValuePair<DateTime, string>>();
                lock (锁)
                {
                    DateTime 最早保留 = DateTime.Today.AddDays(-7);
                    List<DateTime> 过期日期 = new List<DateTime>();
                    foreach (var kv in dayRecords)
                        if (kv.Key < 最早保留) 过期日期.Add(kv.Key);
                    foreach (DateTime d in 过期日期) dayRecords.Remove(d);

                    foreach (var kv in dayRecords)
                        snapshots.Add(new KeyValuePair<DateTime, string>(kv.Key, 生成Csv文本(kv.Value)));
                }
                // 出锁再写文件，不占数据锁；目录不存在则先建（首次运行/被人删了都能自愈）
                Directory.CreateDirectory(ShiftStore.DataDir);
                foreach (var kv in snapshots)
                    File.WriteAllText(ShiftStore.CsvPath(kv.Key), kv.Value, new UTF8Encoding(true));

                // 落盘成功日志做节流：每 30 秒落一次盘，全部写日志会把文件撑爆，按 10 分钟记一条即可
                if ((DateTime.Now - lastSaveLogTime).TotalMinutes >= 10)
                {
                    lastSaveLogTime = DateTime.Now;
                    Log.Info("落盘成功: " + snapshots.Count + " 天数据 → " + ShiftStore.DataDir);
                }
                return "";
            }
            catch (Exception ex)
            {
                Log.Error("保存 CSV 失败（数据目录=" + ShiftStore.DataDir + "）", ex);
                return "数据保存失败:" + ex.Message; // 兜底 catch 必须在界面可见，见标题栏
            }
        }

        /// <summary>把一天的记录拼成 CSV 全文（锁内调用，纯内存操作；与 WinForms 版格式完全一致）</summary>
        public string 生成Csv文本(DayRecord rec)
        {
            var lines = new List<string>();
            lines.Add(string.Join(",", ShiftStore.列标题));
            string[] dayTitles = ShiftStore.ShiftTitles(true);
            string[] nightTitles = ShiftStore.ShiftTitles(false);
            for (int i = 0; i < ShiftStore.RowsPerShift; i++)
                lines.Add(行文本("白班 " + dayTitles[i], rec.DayShift[i]));
            for (int i = 0; i < ShiftStore.RowsPerShift; i++)
                lines.Add(行文本("夜班 " + nightTitles[i], rec.NightShift[i]));
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>一行统计 → CSV 文字（与 ShiftStore.RowText 同格式；数字用 InvariantCulture 固定小数点）</summary>
        private static string 行文本(string 时段, HourStat s)
        {
            return string.Join(",",
                时段, s.TotalCount, s.OkCount, s.NgCount,
                s.UnitOk[0], s.UnitNg[0], s.UnitOk[1], s.UnitNg[1], s.UnitOk[2], s.UnitNg[2],
                (s.ProductionSec / 60.0).ToString("F1", CultureInfo.InvariantCulture),
                (s.WaitSec / 60.0).ToString("F1", CultureInfo.InvariantCulture),
                (s.AlarmSec / 60.0).ToString("F1", CultureInfo.InvariantCulture),
                (s.StopSec / 60.0).ToString("F1", CultureInfo.InvariantCulture));
        }
    }
}
