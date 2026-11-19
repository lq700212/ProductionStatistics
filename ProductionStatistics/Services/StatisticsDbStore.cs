using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace ProductionStatistics
{
    /// <summary>
    /// 统计数据 SQLite 数据库仓库【V2.3.1】—— 数据主存储（CSV 降级为"一次性导入源 + 查询导出格式"）。
    ///
    /// 做什么：
    ///   ① 打开 exe 目录"数据\生产统计.db"（WAL 模式：断电自恢复，现场强杀进程不坏库）；
    ///   ② 保存天：把 DayRecord 的 24 个小时行（白班 12 + 夜班 12）UPSERT 进 hour_stat 表；
    ///   ③ 读取天 / 读取区间：给启动续算与"历史查询"页签取数；
    ///   ④ 导入历史Csv：库为空且发现旧版按天 CSV（生产履历_yyyy-MM-dd.csv）时一次性搬进库，
    ///      现场从旧版升级零手工迁移；导入后 CSV 只当导出格式，不再回读。
    ///
    /// 表结构（hour_stat，一行 = 一个小时桶，主键 date+shift+hour_index）：
    ///   date TEXT 'yyyy-MM-dd' | shift 0=白班(08-20) 1=夜班(20-次日08) | hour_index 0..11
    ///   ok1/ng1..ok3/ng3 三单元计数 | production_sec/wait_sec/alarm_sec/stop_sec 四类时间（秒）。
    ///   时间存原始秒不存分钟：旧 CSV 存分钟会四舍五入丢精度，库里存秒、显示/导出时再换算。
    ///
    /// 线程约定：30 秒落库在采集线程（PlcCollector）触发、退出落库与历史查询在 UI 线程——
    ///   System.Data.SQLite 的连接不是线程安全对象，所有公开方法统一过 _库锁 串行化。
    ///   本锁只保护数据库连接，与 StatisticsStore.锁（保护内存记录）互相独立。
    /// 怎么改：加统计列时，确保表() 建表语句、保存天()、读取行() 三处同步改，并对旧库文件
    ///   补 ALTER TABLE 迁移（SQLite 加列支持 ALTER TABLE ADD COLUMN，见 SQLite 文档）。
    /// </summary>
    public class StatisticsDbStore
    {
        /// <summary>库文件路径：exe 目录 数据\生产统计.db（与旧 CSV 同目录，现场备份/查找都方便）</summary>
        public static string DbPath { get { return Path.Combine(ShiftStore.DataDir, "生产统计.db"); } }

        /// <summary>库级串行锁：采集线程落库 / UI 线程查询与退出落库，统一排队（lock 可重入，导入时二次进入没问题）</summary>
        private readonly object _库锁 = new object();

        /// <summary>库连接：进程生命周期内常开（低流量单进程，常开最简单）；null = 打开失败，保存时自动重试</summary>
        private SQLiteConnection _连接;

        /// <summary>
        /// 打开库并确保表存在。已打开时幂等返回；失败抛异常由调用方记日志（保存路径会自动重试打开）。
        /// </summary>
        public void 打开库()
        {
            lock (_库锁)
            {
                if (_连接 != null) return;
                Directory.CreateDirectory(ShiftStore.DataDir);
                var cs = new SQLiteConnectionStringBuilder
                {
                    DataSource = DbPath,
                    Version = 3,
                    // WAL（Write-Ahead Log）：写不阻塞读、断电后由 -wal 文件自动恢复，是 SQLite
                    // 在工控机"可能被直接拔电"场景下的标准姿势
                    JournalMode = SQLiteJournalModeEnum.Wal,
                    SyncMode = SynchronizationModes.Normal,
                };
                _连接 = new SQLiteConnection(cs.ConnectionString);
                _连接.Open();
                确保表();
            }
        }

        /// <summary>关库（程序退出时调用；平时保持常开，频繁开关反而费时）</summary>
        public void 关闭库()
        {
            lock (_库锁)
            {
                if (_连接 == null) return;
                _连接.Close();
                _连接.Dispose();
                _连接 = null;
            }
        }

        /// <summary>建表（幂等）：主键 (date, shift, hour_index)，按天/按区间读写都走这个键序</summary>
        private void 确保表()
        {
            执行(
                "CREATE TABLE IF NOT EXISTS hour_stat (" +
                "  date TEXT NOT NULL," +                          // 归属日期 yyyy-MM-dd（夜班按 20:00 所在日期归档）
                "  shift INTEGER NOT NULL," +                      // 0=白班(08:00-20:00) 1=夜班(20:00-次日08:00)
                "  hour_index INTEGER NOT NULL," +                 // 班内小时行号 0..11（0 = 08:00-09:00 / 20:00-21:00）
                "  ok1 INTEGER NOT NULL DEFAULT 0, ng1 INTEGER NOT NULL DEFAULT 0," +   // 焊接
                "  ok2 INTEGER NOT NULL DEFAULT 0, ng2 INTEGER NOT NULL DEFAULT 0," +   // 气密
                "  ok3 INTEGER NOT NULL DEFAULT 0, ng3 INTEGER NOT NULL DEFAULT 0," +   // 贴标
                "  production_sec REAL NOT NULL DEFAULT 0," +      // 生产时间（秒）
                "  wait_sec REAL NOT NULL DEFAULT 0," +            // 待料时间（秒）
                "  alarm_sec REAL NOT NULL DEFAULT 0," +           // 报警时间（秒）
                "  stop_sec REAL NOT NULL DEFAULT 0," +            // 停止时间（秒）
                "  PRIMARY KEY (date, shift, hour_index))");
        }

        /// <summary>执行一条无结果 SQL（建表用）</summary>
        private void 执行(string sql)
        {
            using (var cmd = _连接.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 把一天的 24 个小时行整体 UPSERT 进库（INSERT OR REPLACE，与旧 CSV"整文件覆盖"口径一致，
        /// 内存里改多少就库里覆盖成多少，不会出现增量叠加错位）。
        /// </summary>
        public void 保存天(DayRecord rec)
        {
            if (rec == null) return;
            lock (_库锁)
            {
                if (_连接 == null) 打开库();   // 上次打开失败（盘被占用等）时自动重试一次
                using (var tx = _连接.BeginTransaction())
                using (var cmd = _连接.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT OR REPLACE INTO hour_stat(date,shift,hour_index," +
                        "ok1,ng1,ok2,ng2,ok3,ng3,production_sec,wait_sec,alarm_sec,stop_sec) " +
                        "VALUES(@d,@s,@h,@ok1,@ng1,@ok2,@ng2,@ok3,@ng3,@p,@w,@a,@st)";
                    for (int shift = 0; shift < 2; shift++)
                    {
                        HourStat[] rows = shift == 0 ? rec.DayShift : rec.NightShift;
                        for (int h = 0; h < ShiftStore.RowsPerShift; h++)
                        {
                            HourStat s = rows[h];
                            // 参数绑定传值（不拼 SQL 字符串）：防注入之外，还天然规避小数点/引号的区域格式坑
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@d", rec.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@s", shift);
                            cmd.Parameters.AddWithValue("@h", h);
                            cmd.Parameters.AddWithValue("@ok1", s.UnitOk[0]);
                            cmd.Parameters.AddWithValue("@ng1", s.UnitNg[0]);
                            cmd.Parameters.AddWithValue("@ok2", s.UnitOk[1]);
                            cmd.Parameters.AddWithValue("@ng2", s.UnitNg[1]);
                            cmd.Parameters.AddWithValue("@ok3", s.UnitOk[2]);
                            cmd.Parameters.AddWithValue("@ng3", s.UnitNg[2]);
                            cmd.Parameters.AddWithValue("@p", s.ProductionSec);
                            cmd.Parameters.AddWithValue("@w", s.WaitSec);
                            cmd.Parameters.AddWithValue("@a", s.AlarmSec);
                            cmd.Parameters.AddWithValue("@st", s.StopSec);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();   // 一天一个事务：要么 24 行全成、要么全不留，不会写半天的数据
                }
            }
        }

        /// <summary>读某天的记录；库没数据返回 null（当天从零开始记，不是异常）</summary>
        public DayRecord 读取天(DateTime date)
        {
            List<DayRecord> list = 读取区间(date, date);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>
        /// 读日期区间 [起, 止]（含两端）的全部记录，按日期升序。给"历史查询"页签取数；
        /// 只还原有过数据的小时行（空行保持零值，与内存结构一致）。
        /// </summary>
        public List<DayRecord> 读取区间(DateTime 起, DateTime 止)
        {
            var result = new List<DayRecord>();
            lock (_库锁)
            {
                if (_连接 == null) return result;   // 库没打开成功：当无数据返回，由调用方兜底
                using (var cmd = _连接.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT date,shift,hour_index,ok1,ng1,ok2,ng2,ok3,ng3," +
                        "production_sec,wait_sec,alarm_sec,stop_sec FROM hour_stat " +
                        "WHERE date BETWEEN @a AND @b ORDER BY date,shift,hour_index";
                    cmd.Parameters.AddWithValue("@a", 起.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("@b", 止.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DateTime date = DateTime.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                            int shift = Convert.ToInt32(reader.GetInt64(1));
                            int hourIndex = Convert.ToInt32(reader.GetInt64(2));
                            DayRecord rec = 按日期找(result, date);
                            HourStat s = (shift == 0 ? rec.DayShift : rec.NightShift)[hourIndex];
                            s.UnitOk[0] = reader.GetInt32(3);  s.UnitNg[0] = reader.GetInt32(4);
                            s.UnitOk[1] = reader.GetInt32(5);  s.UnitNg[1] = reader.GetInt32(6);
                            s.UnitOk[2] = reader.GetInt32(7);  s.UnitNg[2] = reader.GetInt32(8);
                            s.ProductionSec = reader.GetDouble(9);
                            s.WaitSec = reader.GetDouble(10);
                            s.AlarmSec = reader.GetDouble(11);
                            s.StopSec = reader.GetDouble(12);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>在结果集里找（或补建）某天的记录（读取区间内部小工具，调用方持 _库锁）</summary>
        private static DayRecord 按日期找(List<DayRecord> list, DateTime date)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Date == date) return list[i];
            var rec = new DayRecord { Date = date };
            list.Add(rec);
            return rec;
        }

        /// <summary>库 hour_stat 表是否已有数据（导入历史 CSV 前的判据：有数据绝不二次导入）</summary>
        public bool 已有数据()
        {
            lock (_库锁)
            {
                if (_连接 == null) 打开库();
                using (var cmd = _连接.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM hour_stat";
                    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        /// <summary>
        /// 旧版按天 CSV 一次性导入：仅当库为空时执行（幂等——升级当天搬一次，之后 CSV 不再回读）。
        /// 文件名格式 生产履历_yyyy-MM-dd.csv；坏名/坏行按 ShiftStore.Load 的容错规则跳过。
        /// 返回导入的天数（0 = 库已有数据或没找到 CSV）。
        /// </summary>
        public int 导入历史Csv()
        {
            lock (_库锁)
            {
                if (_连接 == null) 打开库();
                if (已有数据()) return 0;                       // 库非空：绝不覆盖/叠加二次导入
                if (!Directory.Exists(ShiftStore.DataDir)) return 0;
                string[] files = Directory.GetFiles(ShiftStore.DataDir, "生产履历_*.csv");
                int 导入天数 = 0;
                foreach (string f in files)
                {
                    // 从文件名取日期（生产履历_yyyy-MM-dd → 下划线后 10 位）；解析失败跳过，防现场乱名文件
                    string name = Path.GetFileNameWithoutExtension(f);
                    int under = name.IndexOf('_');
                    if (under < 0 || under + 1 + 10 > name.Length) continue;
                    DateTime date;
                    if (!DateTime.TryParseExact(name.Substring(under + 1, 10), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date)) continue;
                    DayRecord rec = ShiftStore.Load(date);
                    if (rec == null) continue;
                    保存天(rec);   // lock 可重入，同线程直接进
                    导入天数++;
                }
                return 导入天数;
            }
        }
    }
}
