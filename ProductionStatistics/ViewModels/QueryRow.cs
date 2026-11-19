using System;
using System.ComponentModel;
using System.Globalization;

namespace ProductionStatistics
{
    /// <summary>
    /// 历史查询结果一行（界面用）【V2.3.1】：比主表 HourRow 多"日期 / 班次"两列，
    /// 数值口径与主表完全一致——时间秒→分钟（1 位小数）、良率/稼动率无产出时显示 0.00。
    ///
    /// 单元筛选在生成行时完成：单元索引 0=全部（三单元合计）1/2/3=焊接/气密/贴标，
    /// 选具体单元时 Ok/Ng 换成该单元的计数，"小时产能 = 该单元 OK+NG"。
    ///
    /// 与 HourRow 的差异：查询结果是按需重建的快照（不随采集实时刷新），
    /// 切语言后由 应用语言() 里重跑一次查询来跟着变，不需要 INPC 逐字段通知以外的东西。
    /// </summary>
    public class QueryRow : INotifyPropertyChanged
    {
        /// <summary>归属日期（yyyy-MM-dd）</summary>
        public string 日期 { get { return dateText; } set { dateText = value; Raise("日期"); } }
        private string dateText = "";

        /// <summary>班次（白班/夜班，随界面语言）</summary>
        public string 班次 { get { return shiftText; } set { shiftText = value; Raise("班次"); } }
        private string shiftText = "";

        /// <summary>时段文字（如 08:00-09:00）</summary>
        public string 时段 { get { return period; } set { period = value; Raise("时段"); } }
        private string period = "";

        /// <summary>小时产能（筛选单元的 OK+NG）</summary>
        public int Total { get { return total; } set { total = value; Raise("Total"); } }
        private int total;

        /// <summary>OK 产能（筛选单元口径）</summary>
        public int Ok { get { return ok; } set { ok = value; Raise("Ok"); } }
        private int ok;

        /// <summary>NG 产能（筛选单元口径）</summary>
        public int Ng { get { return ng; } set { ng = value; Raise("Ng"); } }
        private int ng;

        /// <summary>良率%（2 位小数；无产出显示 0.00，与主表一致）</summary>
        public string Yield { get { return yieldText; } set { yieldText = value; Raise("Yield"); } }
        private string yieldText = "";

        /// <summary>生产时间（分钟，1 位小数）</summary>
        public string Production { get { return production; } set { production = value; Raise("Production"); } }
        private string production = "";

        /// <summary>待料时间（分钟）</summary>
        public string Wait { get { return wait; } set { wait = value; Raise("Wait"); } }
        private string wait = "";

        /// <summary>报警时间（分钟）</summary>
        public string Alarm { get { return alarm; } set { alarm = value; Raise("Alarm"); } }
        private string alarm = "";

        /// <summary>停止时间（分钟）</summary>
        public string Stop { get { return stop; } set { stop = value; Raise("Stop"); } }
        private string stop = "";

        /// <summary>稼动率%（2 位小数；四类时间和为 0 时显示 0.00）</summary>
        public string Utilization { get { return utilization; } set { utilization = value; Raise("Utilization"); } }
        private string utilization = "";

        /// <summary>
        /// 用一条小时数据生成本行全部内容。
        /// 单元索引：-1/0=全部单元（合计）1=焊接 2=气密 3=贴标（与 PlcCollector.单元名称 下标对齐）。
        /// </summary>
        public void 设置(DateTime d, string 班次文字, string 时段文字, HourStat s, int 单元索引)
        {
            int u = 单元索引 < 0 || 单元索引 > 2 ? -1 : 单元索引;
            日期 = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            班次 = 班次文字;
            时段 = 时段文字;
            Ok = u < 0 ? s.OkCount : s.UnitOk[u];
            Ng = u < 0 ? s.NgCount : s.UnitNg[u];
            Total = Ok + Ng;
            Yield = Total > 0
                ? (100.0 * Ok / Total).ToString("F2", CultureInfo.InvariantCulture)
                : "0.00";
            Production = (s.ProductionSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Wait = (s.WaitSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Alarm = (s.AlarmSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Stop = (s.StopSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            double 总时长 = s.ProductionSec + s.WaitSec + s.AlarmSec + s.StopSec;
            Utilization = 总时长 > 0
                ? (100.0 * s.ProductionSec / 总时长).ToString("F2", CultureInfo.InvariantCulture)
                : "0.00";
        }

        /// <summary>属性变化通知（WPF 数据绑定用）</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string 属性名)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(属性名));
        }
    }
}
