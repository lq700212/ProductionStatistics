using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ProductionStatistics
{
    /// <summary>
    /// 主窗体（生产履历统计界面）—— WPF 版。
    ///
    /// 做什么（与 WinForms 版一一对应，界面换皮、逻辑不变）：
    ///   顶栏：设备名 / PLC 连接状态 / 时钟 / 主题开关(深浅) / 中英切换 / 导出 Excel
    ///   页签：当天·昨天·前天 × 白班·夜班 共 6 页（两行文字：班次名 + 日期，跨天自动更新）
    ///   KPI：当前页签的总产能 / OK / NG / 良率 / 稼动率 / 生产时间
    ///   主表：12 个小时行 + 班次合计行，列与现场 HMI 一致
    ///   右侧：设备状态（自动中/报警/稼动率环）+ 三单元 OK/NG/IDLE + 提示
    ///   底栏：设备运行/暂停/报警/离线四态 + 采样与落盘信息
    ///
    /// 运行流程：
    ///   构造 → 建集合 → 载入 ini 与历史 CSV → 启动 PlcCollector（后台轮询 PLC）
    ///         → 启动 1 秒 DispatcherTimer：消费脏标志刷新表格/KPI/单元状态
    ///   关闭 → 停采集 → 最后落一次盘
    ///
    /// 线程纪律：采集线程只写内存 + 置脏标志，**不碰界面**；界面全部由 1 秒定时器在 UI 线程刷新。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ===================== 统计数据与采集 =====================
        /// <summary>统计仓库（内存统计 + CSV 落盘）</summary>
        private StatisticsStore 统计 = new StatisticsStore();

        /// <summary>PLC 采集服务（后台线程）</summary>
        private PlcCollector 采集;

        /// <summary>界面刷新定时器（1 秒）：消费采集线程置的脏标志</summary>
        private DispatcherTimer 刷新定时器 = new DispatcherTimer();

        // ===================== 界面数据集合（供 XAML 绑定） =====================
        /// <summary>表格 13 行（12 小时 + 合计）</summary>
        public ObservableCollection<HourRow> 表格行 { get; private set; }

        /// <summary>6 个班次页签</summary>
        public ObservableCollection<TabView> 页签列表 { get; private set; }

        /// <summary>6 张 KPI 卡片</summary>
        public ObservableCollection<KpiItem> KPI列表 { get; private set; }

        /// <summary>三个单元的实时状态</summary>
        public ObservableCollection<UnitItem> 单元列表 { get; private set; }

        // ===================== 界面状态 =====================
        /// <summary>当前选中的页签序号：0~5 = 当天白/夜、昨天白/夜、前天白/夜</summary>
        private int 选中页签 = 0;

        /// <summary>界面语言：false=中文 true=英文</summary>
        private bool 英文界面 = false;

        /// <summary>上次刷新页签日期的日期值：跨天（过午夜）时自动更新页签上的日期文字</summary>
        private DateTime 上次页签日期 = DateTime.MinValue;

        /// <summary>语言切换保护标志：RadioButton 互斥选中会连带触发事件，避免递归</summary>
        private bool 语言切换中 = false;

        /// <summary>上次落盘时刻（显示在底栏）</summary>
        private DateTime 上次落盘显示 = DateTime.MinValue;

        /// <summary>构造函数：建界面集合、读配置、启采集、启刷新定时器</summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // ① 界面数据集合先建好（KPI 6 张、单元 3 个、表格 13 行、页签 6 个）
            KPI列表 = new ObservableCollection<KpiItem>();
            for (int i = 0; i < 6; i++) KPI列表.Add(new KpiItem());
            单元列表 = new ObservableCollection<UnitItem>();
            for (int i = 0; i < 3; i++)
            {
                单元列表.Add(new UnitItem
                {
                    名称 = PlcCollector.单元名称[i],
                    // 地址直接取 ini 里的地址配置（此时采集服务还没创建，不能用它的实例方法）
                    地址 = AppConfig.地址配置.单元OK[i] + " / " + AppConfig.地址配置.单元NG[i]
                         + " / " + AppConfig.地址配置.单元IDLE[i]
                });
            }
            表格行 = new ObservableCollection<HourRow>();
            for (int i = 0; i < 13; i++) 表格行.Add(new HourRow());
            页签列表 = new ObservableCollection<TabView>();
            for (int i = 0; i < 6; i++) 页签列表.Add(new TabView());

            // ② 读配置（ini 已在 App 启动时读过一遍，这里直接取值）+ 载入历史 CSV 续算
            英文界面 = AppConfig.是英文界面;
            统计.载入最近三天();

            // ③ 建界面文字与页签
            应用语言(英文界面);
            刷新页签文字();

            // ④ 启动 PLC 采集（后台线程）；连接失败也不影响界面
            采集 = new PlcCollector(统计, AppConfig.地址配置);
            采集.连接状态已变化 += (在线) =>
                // 用 BeginInvoke（异步）而不是 Invoke：采集线程不能被 UI 线程阻塞，
                // 否则 UI 线程若正持锁等采集线程，就可能互等（死锁）
                Dispatcher.BeginInvoke(new Action(() => 刷新连接状态(在线)));
            采集.启动(AppConfig.取("PLC", "IP", "192.168.1.156"),
                       AppConfig.取整数("PLC", "Port", 6060),
                       AppConfig.取整数("PLC", "轮询间隔Ms", 100));

            // ⑤ 按屏幕分辨率适配窗口（现场 1280×1024；窗口不能超出屏幕）
            按屏幕适配窗口();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                钉住内容高度();
                按内容设置最小尺寸();
            }), System.Windows.Threading.DispatcherPriority.Background);

            // ⑥ 界面刷新定时器：1 秒一次，消费脏标志（采集线程绝不碰界面）
            刷新定时器.Interval = TimeSpan.FromSeconds(1);
            刷新定时器.Tick += 刷新定时器_Tick;
            刷新定时器.Start();

            Log.Info("主窗体初始化完成: 页签=6 | 数据目录=" + ShiftStore.DataDir + " | 日志目录=" + Log.LogDir);
        }

        /// <summary>1 秒刷新定时器：刷时钟 → 跨天刷页签日期 → 脏标志成立才刷表格/KPI/单元状态 → 刷状态条</summary>
        private void 刷新定时器_Tick(object sender, EventArgs e)
        {
            时钟文字.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

            // 跨天（过了午夜）自动更新页签上的日期文字
            if (DateTime.Today != 上次页签日期) 刷新页签文字();

            // 采集线程置脏标志才刷新（高频采集与低频刷新解耦）
            if (采集 != null && 采集.dataDirty)
            {
                采集.dataDirty = false;
                刷新表格();
                刷新KPI();
                刷新单元状态();
                刷新提示();
            }
            刷新状态条();
            // PLC 连接状态每秒与采集线程的实际状态对齐（不依赖事件是否送达，避免顶栏与实际不一致）
            刷新连接状态(采集 != null && 采集.IsPlcOnline);
        }

        /// <summary>刷新 6 个页签的文字（两行：班次名 + 日期）与选中态</summary>
        private void 刷新页签文字()
        {
            上次页签日期 = DateTime.Today;
            for (int i = 0; i < 页签列表.Count; i++)
            {
                DateTime 日期 = DateTime.Today.AddDays(-(i / 2));
                bool 是白班 = (i % 2) == 0;
                页签列表[i].序号 = i;
                页签列表[i].名称 = 英文界面
                    ? (是白班 ? "Day Shift " + 日期.ToString("MM-dd") : "Night Shift " + 日期.ToString("MM-dd"))
                    : (是白班 ? "当天白班产能" : "当天夜班产能");
                if (i / 2 == 1) 页签列表[i].名称 = 英文界面
                    ? (是白班 ? "D-1 Day " + 日期.ToString("MM-dd") : "D-1 Night " + 日期.ToString("MM-dd"))
                    : (是白班 ? "昨天白班产能" : "昨天夜班产能");
                if (i / 2 == 2) 页签列表[i].名称 = 英文界面
                    ? (是白班 ? "D-2 Day " + 日期.ToString("MM-dd") : "D-2 Night " + 日期.ToString("MM-dd"))
                    : (是白班 ? "前天白班产能" : "前天夜班产能");
                页签列表[i].日期 = 日期.ToString("yyyy-MM-dd");
                页签列表[i].IsSelected = (i == 选中页签);
            }
        }

        /// <summary>刷新表格：12 个小时行 + 班次合计行（锁内取数，出锁后逐行更新属性）</summary>
        private void 刷新表格()
        {
            DateTime date = DateTime.Today.AddDays(-(选中页签 / 2));
            bool 是白班 = (选中页签 % 2) == 0;
            string[] titles = ShiftStore.ShiftTitles(是白班);

            lock (统计.锁)
            {
                DayRecord rec = 统计.取记录(date);
                HourStat[] stats = 是白班 ? rec.DayShift : rec.NightShift;

                double okSum = 0, ngSum = 0, prodSum = 0, waitSum = 0, alarmSum = 0, stopSum = 0;
                for (int i = 0; i < 12; i++)
                {
                    表格行[i].更新(stats[i], titles[i], false);
                    okSum += stats[i].OkCount; ngSum += stats[i].NgCount;
                    prodSum += stats[i].ProductionSec; waitSum += stats[i].WaitSec;
                    alarmSum += stats[i].AlarmSec; stopSum += stats[i].StopSec;
                }
                // 合计行：时段列显示"白班/夜班"（按当前语言，与 HMI 一致）
                var 合计 = new HourStat
                {
                    ProductionSec = prodSum, WaitSec = waitSum,
                    AlarmSec = alarmSum, StopSec = stopSum,
                    UnitOk = new[] { (int)okSum, 0, 0 }, // 合计行只用合计数，分单元列不用
                    UnitNg = new[] { (int)ngSum, 0, 0 }
                };
                表格行[12].更新(合计, 英文界面 ? (是白班 ? "Day Shift" : "Night Shift") : (是白班 ? "白班" : "夜班"), true);
            }
        }

        /// <summary>刷新 6 张 KPI 卡片（当前页签的合计口径）</summary>
        private void 刷新KPI()
        {
            DateTime date = DateTime.Today.AddDays(-(选中页签 / 2));
            bool 是白班 = (选中页签 % 2) == 0;
            double 产能 = 0, ok = 0, ng = 0, 生产 = 0, 待料 = 0, 报警 = 0, 停止 = 0;
            lock (统计.锁)
            {
                DayRecord rec = 统计.取记录(date);
                HourStat[] stats = 是白班 ? rec.DayShift : rec.NightShift;
                for (int i = 0; i < 12; i++)
                {
                    产能 += stats[i].TotalCount; ok += stats[i].OkCount; ng += stats[i].NgCount;
                    生产 += stats[i].ProductionSec; 待料 += stats[i].WaitSec;
                    报警 += stats[i].AlarmSec; 停止 += stats[i].StopSec;
                }
            }
            double 总时长 = 生产 + 待料 + 报警 + 停止;
            double 良率 = 产能 > 0 ? 100.0 * ok / 产能 : 0;
            double 稼动率 = 总时长 > 0 ? 100.0 * 生产 / 总时长 : 0;

            KPI列表[0].设置("M3 3v18h18 M7 14l3-3 3 3 5-6", 英文界面 ? "Total Output" : "总产能",
                ((int)产能).ToString(CultureInfo.InvariantCulture), "PCS",
                (英文界面 ? "OK " : "OK ") + ((int)ok).ToString(CultureInfo.InvariantCulture)
                + " · NG " + ((int)ng).ToString(CultureInfo.InvariantCulture));
            KPI列表[1].设置("M20 6 9 17l-5-5", 英文界面 ? "OK Output" : "OK 产能",
                ((int)ok).ToString(CultureInfo.InvariantCulture), "PCS",
                (英文界面 ? "Share " : "占比 ") + 良率.ToString("F2", CultureInfo.InvariantCulture) + "%");
            KPI列表[2].设置("M18 6 6 18 M6 6l12 12", 英文界面 ? "NG Output" : "NG 产能",
                ((int)ng).ToString(CultureInfo.InvariantCulture), "PCS",
                (英文界面 ? "Share " : "占比 ") + (产能 > 0 ? 100.0 * ng / 产能 : 0).ToString("F2", CultureInfo.InvariantCulture) + "%");
            KPI列表[3].设置("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18", 英文界面 ? "Yield" : "良率",
                良率.ToString("F2", CultureInfo.InvariantCulture), "%",
                (英文界面 ? "Good " : "合格 ") + ((int)ok).ToString(CultureInfo.InvariantCulture));
            KPI列表[4].设置("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 2", 英文界面 ? "Utilization" : "稼动率",
                稼动率.ToString("F2", CultureInfo.InvariantCulture), "%",
                (英文界面 ? "Production " : "生产 ") + (生产 / 60.0).ToString("F1", CultureInfo.InvariantCulture));
            KPI列表[5].设置("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 2", 英文界面 ? "Production Time" : "生产时间",
                (生产 / 60.0).ToString("F1", CultureInfo.InvariantCulture), "Min",
                (英文界面 ? "Wait " : "待料 ") + (待料 / 60.0).ToString("F1", CultureInfo.InvariantCulture)
                + " · Stop " + (停止 / 60.0).ToString("F1", CultureInfo.InvariantCulture));

            // 右侧稼动率圆环（StrokeDashArray 画弧：圆周长 ≈ 144.5）
            double 周长 = 144.5;
            稼动率弧.StrokeDashArray = new DoubleCollection(new double[] { 周长 * 稼动率 / 100.0, 周长 });
            稼动率文字.Text = 稼动率.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>刷新右侧三单元的累计 OK/NG 与实时 IDLE 状态</summary>
        private void 刷新单元状态()
        {
            DateTime NowTime = DateTime.Now;
            bool 是白班 = NowTime.Hour >= 8 && NowTime.Hour < 20;
            DateTime 归属日期 = 是白班 ? NowTime.Date
                                : (NowTime.Hour >= 20 ? NowTime.Date : NowTime.Date.AddDays(-1));
            lock (统计.锁)
            {
                DayRecord rec = 统计.取记录(归属日期);
                HourStat[] stats = 是白班 ? rec.DayShift : rec.NightShift;
                for (int i = 0; i < 3; i++)
                {
                    int okSum = 0, ngSum = 0;
                    for (int h = 0; h < 12; h++) { okSum += stats[h].UnitOk[i]; ngSum += stats[h].UnitNg[i]; }
                    单元列表[i].OK文字 = "OK " + okSum;
                    单元列表[i].NG文字 = "NG " + ngSum;
                }
            }
            for (int i = 0; i < 3; i++)
            {
                bool 空闲 = 采集 != null && 采集.latestUnitIdle[i];
                单元列表[i].状态文字 = 空闲 ? (英文界面 ? "Idle" : "空闲") : (英文界面 ? "Run" : "运行");
            }
        }

        /// <summary>刷新提示卡：报警时段提示 / 离线提示 / 正常提示</summary>
        private void 刷新提示()
        {
            if (采集 != null && !采集.IsPlcOnline)
            {
                提示框.Background = (Brush)FindResource("NgSoftBrush");
                提示框.BorderBrush = (Brush)FindResource("NgBrush");
                提示指示灯.Fill = (Brush)FindResource("NgBrush");
                提示文字.Foreground = (Brush)FindResource("NgBrush");
                提示文字.Text = 英文界面 ? "PLC disconnected, retrying..." : "PLC连接失败……重试中";
                return;
            }
            // 找当前小时有没有报警时间，有就提示出来
            DateTime NowTime = DateTime.Now;
            bool 是白班 = NowTime.Hour >= 8 && NowTime.Hour < 20;
            int hourIndex = 是白班 ? NowTime.Hour - 8 : (NowTime.Hour >= 20 ? NowTime.Hour - 20 : NowTime.Hour + 4);
            DateTime 归属日期 = 是白班 ? NowTime.Date
                                : (NowTime.Hour >= 20 ? NowTime.Date : NowTime.Date.AddDays(-1));
            double 报警秒 = 0;
            lock (统计.锁)
            {
                DayRecord rec = 统计.取记录(归属日期);
                HourStat hs = 是白班 ? rec.DayShift[hourIndex] : rec.NightShift[hourIndex];
                报警秒 = hs.AlarmSec;
            }
            if (报警秒 > 0)
            {
                提示框.Background = (Brush)FindResource("NgSoftBrush");
                提示框.BorderBrush = (Brush)FindResource("NgBrush");
                提示指示灯.Fill = (Brush)FindResource("NgBrush");
                提示文字.Foreground = (Brush)FindResource("NgBrush");
                提示文字.Text = (英文界面 ? "Alarm " : "报警 ") + (报警秒 / 60.0).ToString("F1", CultureInfo.InvariantCulture)
                    + (英文界面 ? " min in this hour" : " 分钟（本时段）");
            }
            else
            {
                提示框.Background = (Brush)FindResource("AccentSoftBrush");
                提示框.BorderBrush = (Brush)FindResource("AccentBrush");
                提示指示灯.Fill = (Brush)FindResource("AccentBrush");
                提示文字.Foreground = (Brush)FindResource("AccentBrush");
                提示文字.Text = 英文界面 ? "No alarm in this hour" : "本时段无报警";
            }
        }

        /// <summary>刷新底部状态条（运行 / 暂停 / 报警 / 离线 四态）与右侧信息</summary>
        private void 刷新状态条()
        {
            if (采集 == null) return;
            if (!采集.IsPlcOnline)
            {
                状态灯.Fill = (Brush)FindResource("NgBrush");
                状态条文字.Foreground = (Brush)FindResource("NgBrush");
                状态条文字.Text = 英文界面 ? "PLC connection failed, retrying...!" : "PLC连接失败……重试中!";
                设备状态徽标.Text = 英文界面 ? "Offline" : "离线";
                设备状态徽标.Foreground = (Brush)FindResource("NgBrush");
            }
            else if (采集.latestAlarm)
            {
                状态灯.Fill = (Brush)FindResource("NgBrush");
                状态条文字.Foreground = (Brush)FindResource("NgBrush");
                状态条文字.Text = 英文界面 ? "Machine ALARM!" : "设备报警中!";
                设备状态徽标.Text = 英文界面 ? "ALARM" : "报警";
                设备状态徽标.Foreground = (Brush)FindResource("NgBrush");
            }
            else if (采集.latestAuto)
            {
                状态灯.Fill = (Brush)FindResource("OkBrush");
                状态条文字.Foreground = (Brush)FindResource("OkBrush");
                状态条文字.Text = 英文界面 ? "Machine RUNNING" : "设备运行中";
                设备状态徽标.Text = 英文界面 ? "RUN" : "运行";
                设备状态徽标.Foreground = (Brush)FindResource("OkBrush");
            }
            else
            {
                状态灯.Fill = (Brush)FindResource("WarnBrush");
                状态条文字.Foreground = (Brush)FindResource("WarnBrush");
                状态条文字.Text = 英文界面 ? "Machine PAUSED!" : "设备暂停中!";
                设备状态徽标.Text = 英文界面 ? "DOWN" : "暂停";
                设备状态徽标.Foreground = (Brush)FindResource("WarnBrush");
            }

            // 自动中 / 报警两个信号的文字
            自动中文字.Text = 采集.latestAuto ? (英文界面 ? "● Running" : "● 运行中") : (英文界面 ? "○ Stop" : "○ 停止");
            自动中文字.Foreground = 采集.latestAuto
                ? (Brush)FindResource("OkBrush")
                : (Brush)FindResource("IdleBrush");
            报警文字.Text = 采集.latestAlarm ? (英文界面 ? "● Alarm" : "● 报警") : (英文界面 ? "Normal" : "正常");
            报警文字.Foreground = 采集.latestAlarm
                ? (Brush)FindResource("NgBrush")
                : (Brush)FindResource("OkBrush");

            // 底栏右侧信息（落盘时间每 30 秒变一次，这里显示上次记录的）
            上次落盘显示 = 统计.lastSaveTime;
            底栏信息.Text = (英文界面 ? "Sample " : "采样 ") + AppConfig.取整数("PLC", "轮询间隔Ms", 100) + "ms · "
                + (英文界面 ? "Last save " : "最近落盘 ")
                + (上次落盘显示 == DateTime.MinValue ? "--" : 上次落盘显示.ToString("HH:mm:ss"))
                + (英文界面 ? " · log: logs\\运行日志_日期.log" : " · 日志 logs\\运行日志_日期.log");
        }

        /// <summary>刷新顶栏的 PLC 连接状态</summary>
        private void 刷新连接状态(bool 在线)
        {
            if (在线)
            {
                连接指示灯.Fill = (Brush)FindResource("OkBrush");
                连接状态文字.Text = 英文界面 ? "PLC connected" : "PLC 已连接";
                连接状态文字.Foreground = (Brush)FindResource("OkBrush");
            }
            else
            {
                连接指示灯.Fill = (Brush)FindResource("NgBrush");
                连接状态文字.Text = 英文界面 ? "PLC connection failed" : "PLC 连接失败";
                连接状态文字.Foreground = (Brush)FindResource("NgBrush");
            }
        }

        /// <summary>页签点击：切换选中页并立即刷新表格</summary>
        private void 页签按钮_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;
            if (btn == null || btn.Tag == null) return;
            int 序号 = (int)btn.Tag;
            选中页签 = 序号;
            for (int i = 0; i < 页签列表.Count; i++) 页签列表[i].IsSelected = (i == 选中页签);
            刷新表格();
            刷新KPI();
            刷新提示();
        }

        /// <summary>主题开关：深色 ⇄ 浅色（即时生效，并写回 ini 供下次启动沿用）</summary>
        private void 主题开关_Click(object sender, RoutedEventArgs e)
        {
            bool 深色 = 主题开关.IsChecked == true;
            App.切换主题(深色 ? App.深色主题 : App.浅色主题);
            AppConfig.保存主题(深色 ? App.深色主题 : App.浅色主题);
            // 主题换了，动态着色的部件要按新主题重刷一次
            刷新连接状态(采集 != null && 采集.IsPlcOnline);
            刷新状态条();
            刷新提示();
        }

        /// <summary>语言按钮：切换中/英文（只影响界面显示，CSV/Excel 文件格式固定中文）</summary>
        private void 语言按钮_Click(object sender, RoutedEventArgs e)
        {
            if (语言切换中) return;
            var btn = sender as RadioButton;
            if (btn == null || btn.Tag == null) return;
            语言切换中 = true;
            try
            {
                应用语言((string)btn.Tag == "English");
            }
            finally
            {
                语言切换中 = false;
            }
        }

        /// <summary>
        /// 按当前语言设置全部界面文字。
        /// 新增界面文字的规则：静态文字集中在这里；动态文字（状态灯、合计行、状态条）在各自刷新方法里用 isEnglish 三目选择，
        /// 两处都要有——漏一处切语言时该词不跟着变（AGENTS.md 的约定）。
        /// </summary>
        private void 应用语言(bool 英文)
        {
            // 设置语言按钮的选中态会连带触发 Checked 事件，这里用保护标志防止递归
            bool 原标志 = 语言切换中;
            语言切换中 = true;
            英文界面 = 英文;
            Title = (英文 ? "Production History - " : "生产履历 - ") + 设备显示名();
            标题文字.Text = 英文 ? "Production History" : "生产履历";
            副标题文字.Text = 英文 ? "Machine Name: " + 设备显示名() : "Machine Name: " + 设备显示名();
            导出按钮.Content = 英文 ? "Export Excel" : "导出 Excel";
            语言中文.IsChecked = !英文;
            语言英文.IsChecked = 英文;

            // 右侧栏静态文字（漏网的那几处：设备状态/自动中/报警/稼动率/单元栏/提示）
            设备状态标题.Text = 英文 ? "Machine Status" : "设备状态";
            自动中标题.Text = 英文 ? "Auto Running" : "设备自动中";
            报警标题.Text = 英文 ? "Alarm" : "设备报警";
            稼动率标题.Text = 英文 ? "Shift Utilization" : "本班稼动率";
            稼动率说明.Text = 英文 ? "Production ÷ Total" : "生产 ÷（生产+待料+报警+停止）";
            稼动率说明.ToolTip = 英文 ? "Production ÷ (Production + Idle + Alarm + Stop)" : "生产 ÷（生产+待料+报警+停止）";
            单元栏标题.Text = 英文 ? "Unit Status / Shift Totals" : "单元状态 / 本班统计";
            提示标题.Text = 英文 ? "Notice" : "提示";
            // 窗口按钮与主题开关的悬浮提示
            主题开关.ToolTip = 英文 ? "Dark / Light theme" : "深色 / 浅色主题";
            最小化按钮.ToolTip = 英文 ? "Minimize" : "最小化";
            最大化按钮.ToolTip = 英文 ? "Maximize / Restore" : "最大化 / 还原";
            关闭按钮.ToolTip = 英文 ? "Close" : "关闭";

            // 表格列头（文件里的表头固定中文，这里只是界面显示）
            string[] 列头 = 英文
                ? new[] { "Period", "Output\n(PCS)", "OK Qty\n(PCS)", "NG Qty\n(PCS)", "Yield%",
                          "Prod. Time\n(Min)", "Idle Time\n(Min)", "Alarm Time\n(Min)", "Stop Time\n(Min)", "Utilization%" }
                : new[] { "时段", "小时产能\n(PCS)", "OK产能\n(PCS)", "NG产能\n(PCS)", "良率%",
                          "生产时间\n(Min)", "待料时间\n(Min)", "报警时间\n(Min)", "停止时间\n(Min)", "稼动率%" };
            for (int i = 0; i < 表格.Columns.Count && i < 列头.Length; i++)
                表格.Columns[i].Header = 列头[i];

            // 单元名（焊接/气密/贴标）也随语言切换
            for (int i = 0; i < 单元列表.Count; i++)
                单元列表[i].名称 = 英文 ? PlcCollector.单元名称En[i] : PlcCollector.单元名称[i];

            刷新页签文字();
            刷新表格();
            刷新KPI();
            刷新单元状态();
            刷新连接状态(采集 != null && 采集.IsPlcOnline);
            刷新状态条();
            刷新提示();
            语言切换中 = 原标志;
        }

        /// <summary>设备显示名（中文界面用中文名，英文界面用 ini 里的英文名；没配则沿用中文名）</summary>
        private string 设备显示名()
        {
            if (!英文界面) return AppConfig.设备名称;
            if (AppConfig.设备名称En.Length > 0) return AppConfig.设备名称En;
            if (AppConfig.设备名称 == "点胶固化收料机") return "Dispensing-Curing-Collecting Machine";
            return AppConfig.设备名称;
        }

        /// <summary>导出 Excel：按当前页签所属日期导出整天白班+夜班完整履历</summary>
        private void 导出按钮_Click(object sender, RoutedEventArgs e)
        {
            DateTime date = DateTime.Today.AddDays(-(选中页签 / 2));
            var dlg = new SaveFileDialog
            {
                Filter = "Excel 文件 (*.xlsx)|*.xlsx",
                FileName = "生产履历_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".xlsx"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                DayRecord rec;
                lock (统计.锁) { rec = 统计.取记录(date); }
                string[] 列头 = new string[10];
                for (int i = 0; i < 10; i++) 列头[i] = 表格.Columns[i].Header.ToString().Replace("\n", "");
                ExcelExporter.导出(dlg.FileName, date, rec, 设备显示名(), 列头);
                MessageBox.Show(this, (英文界面 ? "Exported:\n" : "导出成功：\n") + dlg.FileName,
                    英文界面 ? "Export" : "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("导出 Excel 失败", ex);
                MessageBox.Show(this, (英文界面 ? "Export failed: " : "导出失败：") + ex.Message,
                    英文界面 ? "Export" : "导出", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===================== 自绘窗口（去系统标题栏 + 圆角外框） =====================

        /// <summary>是否处于"工作区最大化"（伪最大化：铺满工作区但不盖住任务栏）</summary>
        private bool 伪最大化中 = false;

        /// <summary>伪最大化之前的窗口尺寸（还原时用，不再写死 1500×860）</summary>
        private double 还原宽 = 1240, 还原高 = 920;

        /// <summary>
        /// 标题栏拖动：按住左键拖动窗口；双击切换最大化/还原。
        /// 最大化状态下 DragMove 会抛异常（窗口已由系统接管），吞掉即可。
        /// </summary>
        private void 标题栏_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { 切换最大化(); return; }
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch { /* 最大化状态下拖动会抛异常，忽略 */ }
            }
        }

        /// <summary>最小化按钮</summary>
        private void 最小化按钮_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>最大化 / 还原按钮</summary>
        private void 最大化按钮_Click(object sender, RoutedEventArgs e)
        {
            切换最大化();
        }

        /// <summary>关闭按钮（与原来的右上角 X 一致：停采集 → 最后落盘 → 退出）</summary>
        private void 关闭按钮_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 切换"工作区最大化 / 还原"。
        /// 不用 WindowState.Maximized：那会盖住任务栏，而且窗口全屏化后圆角与阴影就失效了；
        /// 这里改成按工作区尺寸铺满，视觉上一样，但任务栏还在、圆角阴影也保留。
        /// </summary>
        private void 切换最大化()
        {
            Rect 工作区 = SystemParameters.WorkArea;
            if (伪最大化中)
            {
                伪最大化中 = false;
                应用最大化外观(false);
                WindowState = WindowState.Normal;
                Width = 还原宽; Height = 还原高;
                Left = 工作区.Left + (工作区.Width - 还原宽) / 2;
                Top = 工作区.Top + (工作区.Height - 还原高) / 2;
            }
            else
            {
                还原宽 = Width; 还原高 = Height;      // 记下还原尺寸
                伪最大化中 = true;
                应用最大化外观(true);
                WindowState = WindowState.Normal;
                Left = 工作区.Left; Top = 工作区.Top;
                Width = 工作区.Width; Height = 工作区.Height;
            }
        }

        /// <summary>
        /// 最大化时的外观：**去掉外边距与圆角，四角贴边铺满工作区**。
        /// （原来外框固定留 12px 透明边 + 18px 圆角，最大化后屏幕四周就有一圈"漏边"）
        /// </summary>
        private void 应用最大化外观(bool 最大化)
        {
            if (外框网格 == null || 窗口内容 == null) return;
            窗口内容.Padding = new Thickness(最大化 ? 0 : 12);
            窗口内容.CornerRadius = 最大化 ? new CornerRadius(0) : new CornerRadius(18);
            窗口内容.BorderThickness = 最大化 ? new Thickness(0) : new Thickness(1);
        }

        /// <summary>
        /// 按屏幕分辨率自适应窗口大小（现场是 1280×1024，窗口不能超出屏幕）。
        /// 规则：宽高 = 工作区 - 一点余量，且不超过设计上限（1500×920）；
        ///       最小尺寸也压在工作区内，保证任何分辨率下整窗可见。
        /// </summary>
        private void 按屏幕适配窗口()
        {
            Rect 工作区 = SystemParameters.WorkArea;
            double 宽 = Math.Min(1500, 工作区.Width - 24);
            double 高 = Math.Min(920, 工作区.Height - 24);
            if (宽 < 1080) 宽 = Math.Max(1000, 工作区.Width - 12);
            if (高 < 780) 高 = Math.Max(700, 工作区.Height - 12);

            Width = 宽; Height = 高;
            MinWidth = Math.Min(1080, 工作区.Width - 12);
            // 最小高度 780：低于这个高度，右栏卡片和表格会被裁掉（内容显示不完整）
            MinHeight = Math.Min(780, 工作区.Height - 12);
            还原宽 = 宽; 还原高 = 高;

            // 自己定位（多屏时以工作区为准，居中显示）
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 工作区.Left + Math.Max(0, (工作区.Width - 宽) / 2);
            Top = 工作区.Top + Math.Max(0, (工作区.Height - 高) / 2);
        }

        /// <summary>
        /// 右侧栏宽度随窗口宽度三档收放：
        ///   宽屏 ≥1480 → 320；中等 ≥1240 → 300；窄屏（1280 屏）→ 280。
        /// 窄屏时右侧栏变窄，主表格拿到更多宽度，10 列一屏显示得下。
        /// </summary>
        private void 按窗口宽度调整右栏()
        {
            if (右侧栏列 == null) return;
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double 宽 = w >= 1480 ? 320 : (w >= 1240 ? 300 : 280);
            if (Math.Abs(右侧栏列.Width.Value - 宽) > 0.5)
                右侧栏列.Width = new GridLength(宽);
        }

        /// <summary>
        /// 按**内容的实际高度**设置窗口最小高度：窗口拖到最小也不会把内容裁掉。
        /// 内容高度 = 右侧栏三张卡片 + 表格 13 行 + 顶栏/页签/KPI/状态条（实测约 820 DIP）。
        /// 若工作区比内容还矮（极小屏），则以工作区为准，此时右栏与表格各自内部滚动，
        /// 底部状态条始终可见（内容不会整块丢失）。
        /// </summary>
        private void 按内容设置最小尺寸()
        {
            try
            {
                // 内容完整所需高度：顶栏+页签+KPI+表格 13 行+状态条+留白 ≈ 840
                double 需要 = Math.Max(外框网格.DesiredSize.Height, 840);
                double 上限 = SystemParameters.WorkArea.Height - 8;
                MinHeight = Math.Min(Math.Max(需要, 700), 上限);
            }
            catch { }
        }

        /// <summary>窗口尺寸变化：同步右侧栏宽度档位</summary>
        private void 窗口_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            按窗口宽度调整右栏();
            钉住内容高度();
        }

        /// <summary>
        /// 把根容器的高度钉死成"窗口高度 - 上下留白"。
        /// 显式 Height 会覆盖 DesiredSize（FrameworkElement.MeasureCore 的行为），
        /// 于是**内容再大也不会把窗口顶大**：
        ///   · 表格行数超出 → DataGrid 自己的滚动条；
        ///   · 右栏内容超出 → ScrollViewer 滚动条；
        ///   · 底部状态条永远可见。
        /// </summary>
        private void 钉住内容高度()
        {
            try
            {
                double 目标 = Math.Max(0, ActualHeight);
                if (Math.Abs(外框网格.Height - 目标) > 0.5) 外框网格.Height = 目标;
            }
            catch { }
        }

        /// <summary>
        /// 窗口状态变化：
        ///   ① 外部（Win+↑ / 拖到屏幕顶部）触发的真最大化 → 转成"工作区最大化"，保持任务栏可见；
        ///   ② 最大化图标在"方框 / 两个错开的方框"之间切换。
        /// </summary>
        private void 窗口_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized && !伪最大化中)
            {
                还原宽 = Width; 还原高 = Height;
                伪最大化中 = true;
                Rect 工作区 = SystemParameters.WorkArea;
                WindowState = WindowState.Normal;
                应用最大化外观(true);
                Left = 工作区.Left; Top = 工作区.Top;
                Width = 工作区.Width; Height = 工作区.Height;
                return;
            }
            最大化图标.Data = 伪最大化中
                ? Geometry.Parse("M5.5,5.5 H12.5 V12.5 H5.5 Z M3.5,10.5 V3.5 H10.5")
                : Geometry.Parse("M3.5,3.5 H10.5 V10.5 H3.5 Z");
        }

        /// <summary>关窗：停定时器 → 停采集 → 最后落一次盘</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            刷新定时器.Stop();
            if (采集 != null) 采集.停止();
            统计.保存全部();
            Log.Info("软件退出: 停定时器 → 停采集 → 最后落盘");
            base.OnClosing(e);
        }

        // ===================== 界面用的小数据类（供 XAML 绑定） =====================

        /// <summary>一个班次页签（两行文字：班次名 + 日期）</summary>
        public class TabView : System.ComponentModel.INotifyPropertyChanged
        {
            private string nameText = "", dateText = "";
            private int index;
            private bool selected;

            /// <summary>页签主文字</summary>
            public string 名称 { get { return nameText; } set { nameText = value; Raise("名称"); } }
            /// <summary>页签第二行（日期）</summary>
            public string 日期 { get { return dateText; } set { dateText = value; Raise("日期"); } }
            /// <summary>页签序号（点击时带回给界面）</summary>
            public int 序号 { get { return index; } set { index = value; Raise("序号"); } }
            /// <summary>是否选中</summary>
            public bool IsSelected { get { return selected; } set { selected = value; Raise("IsSelected"); } }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Raise(string 名)
            {
                if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(名));
            }
        }

        /// <summary>一张 KPI 卡片</summary>
        public class KpiItem : System.ComponentModel.INotifyPropertyChanged
        {
            private string icon = "", title = "", value = "0", unit = "", sub = "";

            /// <summary>图标（Path Data，lucide 风格）</summary>
            public string 图标路径 { get { return icon; } set { icon = value; Raise("图标路径"); } }
            /// <summary>卡片标题</summary>
            public string 标题 { get { return title; } set { title = value; Raise("标题"); } }
            /// <summary>大数字</summary>
            public string 数值 { get { return value; } set { this.value = value; Raise("数值"); } }
            /// <summary>单位</summary>
            public string 单位 { get { return unit; } set { unit = value; Raise("单位"); } }
            /// <summary>副说明</summary>
            public string 副说明 { get { return sub; } set { sub = value; Raise("副说明"); } }

            /// <summary>
            /// 一次性设置全部内容（刷新 KPI 时调用）。
            /// 注意：必须走属性赋值（属性 setter 会触发 PropertyChanged），直接给字段赋值界面不会刷新。
            /// </summary>
            public void 设置(string 图标, string 标题文字, string 数值文字, string 单位文字, string 说明文字)
            {
                图标路径 = 图标;
                标题 = 标题文字;
                数值 = 数值文字;
                单位 = 单位文字;
                副说明 = 说明文字;
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Raise(string 名)
            {
                if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(名));
            }
        }

        /// <summary>一个单元的实时状态（右侧栏）</summary>
        public class UnitItem : System.ComponentModel.INotifyPropertyChanged
        {
            private string nameText = "", address = "", okText = "OK 0", ngText = "NG 0", stateText = "运行";

            /// <summary>单元名</summary>
            public string 名称 { get { return nameText; } set { nameText = value; Raise("名称"); } }
            /// <summary>对应 PLC 地址</summary>
            public string 地址 { get { return address; } set { address = value; Raise("地址"); } }
            /// <summary>本班 OK 数量</summary>
            public string OK文字 { get { return okText; } set { okText = value; Raise("OK文字"); } }
            /// <summary>本班 NG 数量</summary>
            public string NG文字 { get { return ngText; } set { ngText = value; Raise("NG文字"); } }
            /// <summary>实时状态（运行 / 空闲）</summary>
            public string 状态文字 { get { return stateText; } set { stateText = value; Raise("状态文字"); } }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Raise(string 名)
            {
                if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(名));
            }
        }
    }
}
