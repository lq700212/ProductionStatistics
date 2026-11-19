using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 统计数据仓库：内存里的"日期 × 班次 × 小时"统计结构 + SQLite 数据库落库【V2.3.1】。
    ///
    /// 做什么：
    ///   ① 启动时打开 SQLite 库（数据\生产统计.db），把 今天/昨天/前天 读回内存，实现跨重启续算；
    ///      库为空且发现旧版按天 CSV 时自动一次性导入（现场从旧版升级零手工迁移）；
    ///   ② 每 30 秒（采集线程触发）把内存记录 UPSERT 进库；程序退出时再落一次；
    ///   ③ 对外提供"取某天的记录"（已加锁）与库仓库 实例（历史查询页签用它按区间取数）。
    ///   【V2.3.1】起不再写 CSV——CSV 降级为一次性导入源与查询导出格式，主存储是 SQLite。
    ///
    /// 线程约定：采集线程写、UI 线程读，落库也读 —— 统一过 锁。
    ///   锁内只做内存拷贝；写库（文件 IO）一律出锁后再做（库连接自身由
    ///   StatisticsDbStore._库锁 串行化，采集线程 30 秒落库与 UI 线程退出落库/查询并发安全）。
    /// </summary>
    public class StatisticsStore
    {
        /// <summary>数据互斥锁：采集线程写、UI 线程读、落库读，统一过锁</summary>
        public readonly object 锁 = new object();

        /// <summary>内存里的统计数据，按日期索引（夜班归属 20:00 所在日期）</summary>
        public Dictionary<DateTime, DayRecord> dayRecords = new Dictionary<DateTime, DayRecord>();

        /// <summary>上次落库时刻，控制"每 30 秒存一次库"（PlcCollector 采集循环里判断）</summary>
        public DateTime lastSaveTime = DateTime.MinValue;

        /// <summary>上次成功落库并写日志的时刻：落库每 30 秒一次，全部写日志会刷屏，按这个节流</summary>
        public DateTime lastSaveLogTime = DateTime.MinValue;

        /// <summary>SQLite 数据库仓库（数据主存储）【V2.3.1】</summary>
        public readonly StatisticsDbStore 库 = new StatisticsDbStore();

        /// <summary>
        /// 启动：打开库 →（库空时）一次性导入历史 CSV → 把 最近三天 读回内存续算。
        /// 文件/库不存在就建空白记录，不报错；打开库失败只记日志不崩（保存路径会自动重试）。
        /// </summary>
        public void 载入最近三天()
        {
            StringBuilder 概况 = new StringBuilder();
            int 导入天数 = 0;
            string 打库异常 = null;
            try
            {
                库.打开库();
                导入天数 = 库.导入历史Csv();   // 库为空且有旧 CSV 才真正搬数据（幂等）
            }
            catch (Exception ex)
            {
                打库异常 = ex.Message;
                Log.Error("打开统计数据库失败（" + StatisticsDbStore.DbPath + "），本次以空白记录启动", ex);
            }
            lock (锁)
            {
                for (int i = 0; i < 3; i++)
                {
                    DateTime date = DateTime.Today.AddDays(-i);
                    // 打库成功走库读取；失败（或该天无记录）= 从零开始记（不是续算）
                    DayRecord rec = 打库异常 == null ? 库.读取天(date) : null;
                    bool 无历史 = (rec == null);
                    if (rec == null) rec = new DayRecord { Date = date };
                    dayRecords[date] = rec;
                    概况.Append(date.ToString("yyyy-MM-dd")).Append(无历史 ? "(无历史,新建)" : "(已续算)").Append("  ");
                }
            }
            Log.Info("载入历史数据(最近三天, SQLite" + (导入天数 > 0 ? ",已导入历史CSV " + 导入天数 + " 天" : "") + "): " + 概况.ToString());
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
        /// 把内存里全部天数 UPSERT 进 SQLite 库；失败原因记入返回值供标题栏提示。
        /// 内存只保留最近 7 天（页签只用 3 天，留 4 天余量），更早数据已在库里，不丢。
        /// </summary>
        public string 保存全部()
        {
            try
            {
                // 锁内只拷快照（深拷贝 DayRecord，24 行 × ≤7 天，纯内存毫秒级，不长期占数据锁）
                List<DayRecord> snapshots = new List<DayRecord>();
                lock (锁)
                {
                    DateTime 最早保留 = DateTime.Today.AddDays(-7);
                    List<DateTime> 过期日期 = new List<DateTime>();
                    foreach (var kv in dayRecords)
                        if (kv.Key < 最早保留) 过期日期.Add(kv.Key);
                    foreach (DateTime d in 过期日期) dayRecords.Remove(d);

                    foreach (var kv in dayRecords)
                        snapshots.Add(复制记录(kv.Value));
                }
                // 出锁再写库，不占数据锁；连接内部 _库锁 串行化，采集线程/退出线程并发安全
                foreach (var rec in snapshots)
                    库.保存天(rec);

                // 落库成功日志做节流：每 30 秒落一次，全部写日志会把文件撑爆，按 10 分钟记一条即可
                if ((DateTime.Now - lastSaveLogTime).TotalMinutes >= 10)
                {
                    lastSaveLogTime = DateTime.Now;
                    Log.Info("落库成功: " + snapshots.Count + " 天数据 → " + StatisticsDbStore.DbPath);
                }
                return "";
            }
            catch (Exception ex)
            {
                Log.Error("落库失败（" + StatisticsDbStore.DbPath + "）", ex);
                return "数据保存失败:" + ex.Message; // 兜底 catch 必须在界面可见，见标题栏
            }
        }

        /// <summary>
        /// 深拷贝一天的记录（锁内用）：出锁后慢慢写库，期间采集线程继续改原记录也不影响快照。
        /// </summary>
        private static DayRecord 复制记录(DayRecord r)
        {
            var copy = new DayRecord { Date = r.Date };
            复制班(r.DayShift, copy.DayShift);
            复制班(r.NightShift, copy.NightShift);
            return copy;
        }

        /// <summary>复制一个班次 12 个小时桶（复制记录内部小工具）</summary>
        private static void 复制班(HourStat[] from, HourStat[] to)
        {
            for (int i = 0; i < from.Length; i++)
            {
                to[i].UnitOk = (int[])from[i].UnitOk.Clone();
                to[i].UnitNg = (int[])from[i].UnitNg.Clone();
                to[i].ProductionSec = from[i].ProductionSec;
                to[i].WaitSec = from[i].WaitSec;
                to[i].AlarmSec = from[i].AlarmSec;
                to[i].StopSec = from[i].StopSec;
            }
        }
    }
}
