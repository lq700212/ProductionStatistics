using System;
using System.ComponentModel;
using System.Globalization;

namespace ProductionStatistics
{
    /// <summary>
    /// 表格一行（界面用）：把 HourStat 的秒换算成分钟、算好良率与稼动率，
    /// 直接喂给 DataGrid 绑定。
    ///
    /// 为什么不让 DataGrid 直接绑 HourStat：
    ///   ① 分钟值要"秒/60"再保留 1 位小数，XAML 里写转换器太啰嗦；
    ///   ② 良率/稼动率在没产出时必须显示 0.00 而不是空白；
    ///   ③ 合计行要单独的样式（IsTotal = true），用 HourStat 表达不了。
    ///
    /// 刷新的方式：采集线程置脏标志 → 界面定时器消费 → 逐行更新属性（属性变 → INPC 通知 → 界面自动刷新），
    ///   不重建 DataGrid 的行（避免闪烁），和 WinForms 版"只改单元格不重建行"的规矩一致。
    /// </summary>
    public class HourRow : INotifyPropertyChanged
    {
        /// <summary>时段文字（如 08:00-09:00；合计行是"白班/夜班"）</summary>
        public string Period { get { return period; } set { period = value; Raise("Period"); } }
        private string period;

        /// <summary>小时产能（OK+NG）</summary>
        public int Total { get { return total; } set { total = value; Raise("Total"); } }
        private int total;

        /// <summary>OK 产能</summary>
        public int Ok { get { return ok; } set { ok = value; Raise("Ok"); } }
        private int ok;

        /// <summary>NG 产能</summary>
        public int Ng { get { return ng; } set { ng = value; Raise("Ng"); } }
        private int ng;

        /// <summary>良率%（字符串，保留 2 位小数）</summary>
        public string Yield { get { return yieldText; } set { yieldText = value; Raise("Yield"); } }
        private string yieldText;

        /// <summary>生产时间（分钟，字符串 1 位小数）</summary>
        public string Production { get { return production; } set { production = value; Raise("Production"); } }
        private string production;

        /// <summary>待料时间（分钟）</summary>
        public string Wait { get { return wait; } set { wait = value; Raise("Wait"); } }
        private string wait;

        /// <summary>报警时间（分钟）</summary>
        public string Alarm { get { return alarm; } set { alarm = value; Raise("Alarm"); } }
        private string alarm;

        /// <summary>停止时间（分钟）</summary>
        public string Stop { get { return stop; } set { stop = value; Raise("Stop"); } }
        private string stop;

        /// <summary>稼动率%（字符串，保留 2 位小数）</summary>
        public string Utilization { get { return utilization; } set { utilization = value; Raise("Utilization"); } }
        private string utilization;

        /// <summary>是否合计行（合计行样式不同：主色底 + 加粗）</summary>
        public bool IsTotal { get { return isTotal; } set { isTotal = value; Raise("IsTotal"); } }
        private bool isTotal;

        /// <summary>用一份 HourStat 刷新本行的全部数值（时间按秒→分钟，保留 1 位小数）</summary>
        public void 更新(HourStat s, string 时段文字, bool 是否合计)
        {
            Period = 时段文字;
            Total = s.TotalCount;
            Ok = s.OkCount;
            Ng = s.NgCount;
            Yield = (s.OkCount + s.NgCount) > 0
                ? (100.0 * s.OkCount / (s.OkCount + s.NgCount)).ToString("F2", CultureInfo.InvariantCulture)
                : "0.00";
            Production = (s.ProductionSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Wait = (s.WaitSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Alarm = (s.AlarmSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            Stop = (s.StopSec / 60.0).ToString("F1", CultureInfo.InvariantCulture);
            double 总时长 = s.ProductionSec + s.WaitSec + s.AlarmSec + s.StopSec;
            Utilization = 总时长 > 0
                ? (100.0 * s.ProductionSec / 总时长).ToString("F2", CultureInfo.InvariantCulture)
                : "0.00";
            IsTotal = 是否合计;
        }

        /// <summary>属性变化通知（WPF 数据绑定用）</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string 属性名)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(属性名));
        }
    }
}
