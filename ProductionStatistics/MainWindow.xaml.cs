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
using Kaleidoscope.UI.Views;
using Microsoft.Win32;

namespace ProductionStatistics
{
    /// <summary>
    /// 主窗体（生产履历统计界面）—— WPF 版。
    ///
    /// 做什么（与 WinForms 版一一对应，界面换皮、逻辑不变）：
    ///   顶栏：设备名 / PLC 连接状态 / 时钟 / 主题开关(深浅) / 中英切换 / 导出 Excel
    ///   页签：当天·昨天·前天 × 白班·夜班 共 6 页（两行文字：班次名 + 日期，跨天自动更新）
    ///        + 第 7 页"历史查询"（按日期区间+单元从数据库查询、导出 CSV）【V2.3.1】
    ///   KPI：当前页签的总产能 / OK / NG / 良率 / 稼动率 / 生产时间
    ///   主表：12 个小时行 + 班次合计行，列与现场 HMI 一致
    ///   右侧：设备状态（自动中/报警/稼动率环）+ 三单元 OK/NG/IDLE + 提示
    ///   底栏：设备运行/暂停/报警/离线四态 + 采样与落盘信息
    ///
    /// 运行流程：
    ///   构造 → 建集合 → 载入 ini 与 SQLite 历史（库空时自动导入旧 CSV）→ 启动 PlcCollector（后台轮询 PLC）
    ///         → 启动 1 秒 DispatcherTimer：消费脏标志刷新表格/KPI/单元状态
    ///   关闭 → 停采集 → 最后落库 → 关库
    ///
    /// 线程纪律：采集线程只写内存 + 置脏标志，**不碰界面**；界面全部由 1 秒定时器在 UI 线程刷新。
    ///
    /// 【V2.3.0 基类迁移】继承 kaleidoscope 的 ChromeBorderlessWindow：
    ///   无边框/透明/WindowChrome/根 Border 圆角 Clip/最大化工作区修正全部交给基类，
    ///   本类不再手写伪最大化（原三段互锁：伪最大化 + 应用最大化外观 + 钉住内容高度已拆）。
    /// </summary>
    public partial class MainWindow : ChromeBorderlessWindow
    {
        // ===================== 统计数据与采集 =====================
        /// <summary>统计仓库（内存统计 + SQLite 落库【V2.3.1】）</summary>
        private StatisticsStore 统计 = new StatisticsStore();

        /// <summary>PLC 采集服务（后台线程）</summary>
        private PlcCollector 采集;

        /// <summary>界面刷新定时器（1 秒）：消费采集线程置的脏标志</summary>
        private DispatcherTimer 刷新定时器 = new DispatcherTimer();

        // ===================== 界面数据集合（供 XAML 绑定） =====================
        /// <summary>表格 13 行（12 小时 + 合计）</summary>
        public ObservableCollection<HourRow> 表格行 { get; private set; }

        /// <summary>6 个班次页签 + 第 7 个"历史查询"工具页签【V2.3.1】</summary>
        public ObservableCollection<TabView> 页签列表 { get; private set; }

        /// <summary>历史查询结果行集合【V2.3.1】：查询按钮按需重建内容（Clear+Add），绑定不换实例</summary>
        public ObservableCollection<QueryRow> 查询行 { get; private set; }

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
            // 7 个页签 = 6 个班次页签（今天/昨天/前天 × 白/夜）+ 第 7 个"历史查询"工具页签【V2.3.1】
            for (int i = 0; i < 7; i++) 页签列表.Add(new TabView());
            查询行 = new ObservableCollection<QueryRow>();
            // 历史查询面板默认查"今天"（起止都选今天，切进页签时 切换历史视图 会自动执行一次查询）
            查询起始.SelectedDate = DateTime.Today;
            查询截止.SelectedDate = DateTime.Today;

            // ② 读配置（ini 已在 App 启动时读过一遍，这里直接取值）+ 载入历史 CSV 续算
            英文界面 = AppConfig.是英文界面;
            统计.载入最近三天();
            // 主题开关初始态必须与 ini 实际主题对齐：否则深色启动时开关显示"浅色"，
            // 用户点一下以为切深色、实际被切回浅色（开关与界面反相）。
            主题开关.IsChecked = AppConfig.取主题() == App.深色主题;

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
                钉住内容高度(ActualHeight);
                按内容设置最小尺寸();
            }), System.Windows.Threading.DispatcherPriority.Background);

            // ⑥ 界面刷新定时器：1 秒一次，消费脏标志（采集线程绝不碰界面）
            刷新定时器.Interval = TimeSpan.FromSeconds(1);
            刷新定时器.Tick += 刷新定时器_Tick;
            刷新定时器.Start();

            Log.Info("主窗体初始化完成: 页签=7(含历史查询) | 数据库=" + StatisticsDbStore.DbPath
                + " | 数据目录=" + ShiftStore.DataDir + " | 日志目录=" + Log.LogDir);
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

        /// <summary>刷新 7 个页签的文字（6 个班次页签：班次名+日期；第 7 个=历史查询）与选中态</summary>
        private void 刷新页签文字()
        {
            上次页签日期 = DateTime.Today;
            for (int i = 0; i < 页签列表.Count; i++)
            {
                页签列表[i].序号 = i;
                if (i == 6)   // 第 7 个：历史查询工具页签，不属于任何班次【V2.3.1】
                {
                    页签列表[i].名称 = 英文界面 ? "History Query" : "历史查询";
                    页签列表[i].日期 = 英文界面 ? "Query & CSV" : "查询 · 导出";
                    页签列表[i].IsSelected = (i == 选中页签);
                    continue;
                }
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
            // 历史查询页签下主表格是隐藏的，不必刷（查询面板有自己的数据）【V2.3.1】
            if (选中页签 == 6) return;
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
            // 历史查询页签下 KPI 区是隐藏的，不必刷【V2.3.1】
            if (选中页签 == 6) return;
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

        /// <summary>
        /// 设置提示框语义态：Tag 契约 = 库 KxInfoBox（底/描边）+ KxStatusDot（灯色）+ StatusChipText（文字色）。
        /// 三处必须同步设同一个 Tag，否则会出现"底色红、文字灰"的错位。
        /// 语义：Pri=正常（主色）/ Ng=离线或报警（红）。
        /// </summary>
        private void 设置提示(string 语义, string 文字)
        {
            提示框.Tag = 语义;
            提示指示灯.Tag = 语义;
            提示文字.Tag = 语义;
            提示文字.Text = 文字;
        }

        /// <summary>刷新提示卡：报警时段提示 / 离线提示 / 正常提示</summary>
        private void 刷新提示()
        {
            if (采集 != null && !采集.IsPlcOnline)
            {
                设置提示("Ng", 英文界面 ? "PLC disconnected, retrying..." : "PLC连接失败……重试中");
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
                设置提示("Ng", (英文界面 ? "Alarm " : "报警 ") + (报警秒 / 60.0).ToString("F1", CultureInfo.InvariantCulture)
                    + (英文界面 ? " min in this hour" : " 分钟（本时段）"));
            }
            else
            {
                设置提示("Pri", 英文界面 ? "No alarm in this hour" : "本时段无报警");
            }
        }

        /// <summary>设置底栏状态语义（KxStatusDot 灯色 + StatusChipText 文字色，Tag 必须同步）。</summary>
        private void 设置状态条(string 语义, string 文字)
        {
            状态灯.Tag = 语义;
            状态条文字.Tag = 语义;
            状态条文字.Text = 文字;
        }

        /// <summary>设置设备状态徽标语义（StatusChipText 的 Tag 驱动颜色）。</summary>
        private void 设置徽标(string 语义, string 文字)
        {
            设备状态徽标.Tag = 语义;
            设备状态徽标.Text = 文字;
        }

        /// <summary>设置任意 TextBlock 的语义色与文字（Tag=null 走 StatusChipText 默认灰）。</summary>
        private static void 设置文字语义(TextBlock 目标, string 语义, string 文字)
        {
            目标.Tag = 语义;
            目标.Text = 文字;
        }

        /// <summary>刷新底部状态条（运行 / 暂停 / 报警 / 离线 四态）与右侧信息</summary>
        private void 刷新状态条()
        {
            if (采集 == null) return;
            if (!采集.IsPlcOnline)
            {
                设置状态条("Ng", 英文界面 ? "PLC connection failed, retrying...!" : "PLC连接失败……重试中!");
                设置徽标("Ng", 英文界面 ? "Offline" : "离线");
            }
            else if (采集.latestAlarm)
            {
                设置状态条("Ng", 英文界面 ? "Machine ALARM!" : "设备报警中!");
                设置徽标("Ng", 英文界面 ? "ALARM" : "报警");
            }
            else if (采集.latestAuto)
            {
                设置状态条("Ok", 英文界面 ? "Machine RUNNING" : "设备运行中");
                设置徽标("Ok", 英文界面 ? "RUN" : "运行");
            }
            else
            {
                设置状态条("Warn", 英文界面 ? "Machine PAUSED!" : "设备暂停中!");
                设置徽标("Warn", 英文界面 ? "DOWN" : "暂停");
            }

            // 自动中 / 报警两个信号的文字（Tag=null → StatusChipText 默认灰，对应"停止"）
            设置文字语义(自动中文字, 采集.latestAuto ? "Ok" : null,
                采集.latestAuto ? (英文界面 ? "● Running" : "● 运行中") : (英文界面 ? "○ Stop" : "○ 停止"));
            设置文字语义(报警文字, 采集.latestAlarm ? "Ng" : "Ok",
                采集.latestAlarm ? (英文界面 ? "● Alarm" : "● 报警") : (英文界面 ? "Normal" : "正常"));

            // 底栏右侧信息（落库时间每 30 秒变一次，这里显示上次记录的）【V2.3.1】落盘改落库
            上次落盘显示 = 统计.lastSaveTime;
            底栏信息.Text = (英文界面 ? "Sample " : "采样 ") + AppConfig.取整数("PLC", "轮询间隔Ms", 100) + "ms · "
                + (英文界面 ? "Last saved " : "最近落库 ")
                + (上次落盘显示 == DateTime.MinValue ? "--" : 上次落盘显示.ToString("HH:mm:ss"))
                + (英文界面 ? " · log: logs\\运行日志_日期.log" : " · 日志 logs\\运行日志_日期.log");
        }

        /// <summary>刷新顶栏的 PLC 连接状态（StatusChip 三件套 Tag 同步：胶囊/灯/文字）。</summary>
        private void 刷新连接状态(bool 在线)
        {
            string 语义 = 在线 ? "Ok" : "Ng";
            连接胶囊.Tag = 语义;
            连接指示灯.Tag = 语义;
            连接状态文字.Tag = 语义;
            连接状态文字.Text = 在线
                ? (英文界面 ? "PLC connected" : "PLC 已连接")
                : (英文界面 ? "PLC connection failed" : "PLC 连接失败");
        }

        /// <summary>页签点击：班次页签刷表格/KPI；历史查询页签换查询面板【V2.3.1】</summary>
        private void 页签按钮_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as ToggleButton;
            if (btn == null || btn.Tag == null) return;
            int 序号 = (int)btn.Tag;
            选中页签 = 序号;
            for (int i = 0; i < 页签列表.Count; i++) 页签列表[i].IsSelected = (i == 选中页签);
            // 第 7 个页签=历史查询：KPI 区/主表格换成查询面板；其余页签恢复班次视图
            切换历史视图(序号 == 6);
            if (序号 != 6)
            {
                刷新表格();
                刷新KPI();
            }
            刷新提示();
        }

        /// <summary>
        /// 班次视图 ⇄ 历史查询视图切换【V2.3.1】：只换主区可见性（顶栏/页签栏/右侧栏/状态条共用不动）。
        /// 进历史查询时默认查今天并立即执行，让用户一进来就有数；顶栏"导出 Excel"按班次日期导整天、
        /// 历史页没有"当前班次"概念，禁用避免歧义（导出走查询面板的"导出 CSV"按钮）。
        /// </summary>
        private void 切换历史视图(bool 历史态)
        {
            KPI区.Visibility = 历史态 ? Visibility.Collapsed : Visibility.Visible;
            表格卡片.Visibility = 历史态 ? Visibility.Collapsed : Visibility.Visible;
            查询面板.Visibility = 历史态 ? Visibility.Visible : Visibility.Collapsed;
            导出按钮.IsEnabled = !历史态;
            if (历史态)
            {
                查询起始.SelectedDate = DateTime.Today;
                查询截止.SelectedDate = DateTime.Today;
                执行查询();
            }
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

            // 历史查询面板【V2.3.1】：标签/按钮/列头全量切语言；单元下拉换文字但保持选中项
            查询从标签.Text = 英文 ? "From" : "从";
            查询至标签.Text = 英文 ? "To" : "至";
            查询单元标签.Text = 英文 ? "Unit" : "单元";
            int 查询单元选中 = 查询单元.SelectedIndex;
            查询单元.ItemsSource = 英文
                ? new[] { "All Units", PlcCollector.单元名称En[0], PlcCollector.单元名称En[1], PlcCollector.单元名称En[2] }
                : new[] { "全部单元", PlcCollector.单元名称[0], PlcCollector.单元名称[1], PlcCollector.单元名称[2] };
            查询单元.SelectedIndex = 查询单元选中 < 0 ? 0 : 查询单元选中;
            查询按钮.Content = 英文 ? "Query" : "查询";
            查询导出按钮.Content = 英文 ? "Export CSV" : "导出 CSV";
            导出按钮.ToolTip = 英文 ? "Export current shift day to Excel" : "导出当前页签所属日期的 Excel";
            string[] 查询列头 = 英文
                ? new[] { "Date", "Shift", "Period", "Output\n(PCS)", "OK Qty\n(PCS)", "NG Qty\n(PCS)", "Yield%",
                          "Prod. Time\n(Min)", "Idle Time\n(Min)", "Alarm Time\n(Min)", "Stop Time\n(Min)", "Utilization%" }
                : new[] { "日期", "班次", "时段", "小时产能\n(PCS)", "OK产能\n(PCS)", "NG产能\n(PCS)", "良率%",
                          "生产时间\n(Min)", "待料时间\n(Min)", "报警时间\n(Min)", "停止时间\n(Min)", "稼动率%" };
            for (int i = 0; i < 查询表格.Columns.Count && i < 查询列头.Length; i++)
                查询表格.Columns[i].Header = 查询列头[i];

            刷新页签文字();
            刷新表格();
            刷新KPI();
            刷新单元状态();
            刷新连接状态(采集 != null && 采集.IsPlcOnline);
            刷新状态条();
            刷新提示();
            // 历史查询结果里的"班次"等文字随语言变：结果快照按新语言重跑一次当前查询【V2.3.1】
            if (选中页签 == 6) 执行查询();
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

        // ===================== 历史查询（第 7 页签）【V2.3.1】 =====================

        /// <summary>查询按钮：按面板里的日期区间 + 单元筛选取数（内存优先、库里补齐）</summary>
        private void 查询按钮_Click(object sender, RoutedEventArgs e)
        {
            执行查询();
        }

        /// <summary>
        /// 执行历史查询：日期区间 [起,止]（起>止自动纠正、最长 366 天防手滑拖死整库）× 单元筛选。
        /// 取数顺序：内存里有的日期直接用（今天/昨天/前天实时最新），缺的日期出锁后查 SQLite 补齐——
        /// 锁内绝不碰库（文件 IO），符合"锁内禁止网络/慢 IO"的线程纪律。
        /// 空行（计数与四类时间全 0）不显示，避免历史日期一大片 0 没法看。
        /// </summary>
        private void 执行查询()
        {
            DateTime 起 = 查询起始.SelectedDate ?? DateTime.Today;
            DateTime 止 = 查询截止.SelectedDate ?? DateTime.Today;
            if (起 > 止) { DateTime t = 起; 起 = 止; 止 = t; }
            if ((止 - 起).TotalDays > 366) 止 = 起.AddDays(366);
            int 单元 = 查询单元.SelectedIndex - 1;   // 0=全部单元→-1（三单元合计），1/2/3=焊接/气密/贴标

            var rows = new List<QueryRow>();
            var 缺日期 = new List<DateTime>();
            lock (统计.锁)
            {
                for (DateTime d = 起; d <= 止; d = d.AddDays(1))
                {
                    DayRecord rec;
                    if (统计.dayRecords.TryGetValue(d, out rec))
                        追加查询行(rows, d, rec, 单元);
                    else
                        缺日期.Add(d);
                }
            }
            // 出锁再读库（连接内部 _库锁 串行化），不占 统计.锁
            foreach (DateTime d in 缺日期)
            {
                DayRecord rec = 统计.库.读取天(d);
                if (rec != null) 追加查询行(rows, d, rec, 单元);
            }

            查询行.Clear();
            for (int i = 0; i < rows.Count; i++) 查询行.Add(rows[i]);
            查询结果数.Text = 英文界面 ? rows.Count + " rows" : rows.Count + " 行";
        }

        /// <summary>把一天的记录按单元筛选转成查询行（白班+夜班各 12 个小时桶，空行跳过）</summary>
        private void 追加查询行(List<QueryRow> rows, DateTime d, DayRecord rec, int 单元)
        {
            追加班查询行(rows, d, true, rec.DayShift, 单元);
            追加班查询行(rows, d, false, rec.NightShift, 单元);
        }

        /// <summary>追加一个班次的小时行（追加查询行内部小工具）</summary>
        private void 追加班查询行(List<QueryRow> rows, DateTime d, bool 白班, HourStat[] stats, int 单元)
        {
            string[] titles = ShiftStore.ShiftTitles(白班);
            string 班次文字 = 英文界面 ? (白班 ? "Day" : "Night") : (白班 ? "白班" : "夜班");
            for (int i = 0; i < stats.Length; i++)
            {
                HourStat s = stats[i];
                bool 有数据 = s.TotalCount > 0 || s.ProductionSec > 0 || s.WaitSec > 0
                    || s.AlarmSec > 0 || s.StopSec > 0;
                if (!有数据) continue;
                var row = new QueryRow();
                row.设置(d, 班次文字, titles[i], s, 单元);
                rows.Add(row);
            }
        }

        /// <summary>导出 CSV：把当前查询结果存成 UTF-8(BOM) 文件，列头与查询表格显示一致（随语言）</summary>
        private void 查询导出按钮_Click(object sender, RoutedEventArgs e)
        {
            if (查询行.Count == 0)
            {
                MessageBox.Show(this, 英文界面 ? "No rows. Click Query first." : "暂无查询结果，请先点\"查询\"。",
                    英文界面 ? "Export" : "导出", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "生产履历查询_"
                    + (查询起始.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + "_" + (查询截止.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 查询表格.Columns.Count; i++)
                {
                    sb.Append(查询表格.Columns[i].Header.ToString().Replace("\n", ""));
                    sb.Append(i < 查询表格.Columns.Count - 1 ? "," : "\r\n");
                }
                foreach (QueryRow r in 查询行)
                {
                    // 数字已经是 InvariantCulture 格式化好的字符串（QueryRow.设置），直接拼不丢精度
                    sb.Append(r.日期).Append(',').Append(r.班次).Append(',').Append(r.时段).Append(',')
                      .Append(r.Total).Append(',').Append(r.Ok).Append(',').Append(r.Ng).Append(',')
                      .Append(r.Yield).Append(',').Append(r.Production).Append(',').Append(r.Wait).Append(',')
                      .Append(r.Alarm).Append(',').Append(r.Stop).Append(',').Append(r.Utilization)
                      .Append("\r\n");
                }
                // UTF-8 带 BOM：Excel 双击打开不乱码（与旧 CSV 口径一致）
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Log.Info("历史查询导出 CSV: " + dlg.FileName + " (" + 查询行.Count + " 行)");
                MessageBox.Show(this, (英文界面 ? "Exported:\n" : "导出成功：\n") + dlg.FileName,
                    英文界面 ? "Export" : "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("历史查询导出 CSV 失败", ex);
                MessageBox.Show(this, (英文界面 ? "Export failed: " : "导出失败：") + ex.Message,
                    英文界面 ? "Export" : "导出", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===================== 自绘窗口（外壳由 ChromeBorderlessWindow 基类负责） =====================

        /// <summary>
        /// 标题栏拖动：按住左键拖动窗口；双击切换最大化/还原。
        /// 基类 CaptionHeight=0（顶栏是业务内容，按钮多），所以系统标题区命中为 0，
        /// 拖动/双击由这里手动处理；最大化状态下 DragMove 会抛异常，吞掉即可。
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
        /// 切换最大化 / 还原：直接切 WindowState（真最大化）。
        /// 【V2.3.0】原伪最大化（工作区铺满 + 手动归零 Padding/圆角 + 钉高度）已拆除：
        ///   · 不盖任务栏 = 基类 WM_GETMINMAXINFO 工作区修正（FixMaximizedToWorkArea 缺省开）；
        ///   · 最大化归零根 Border 圆角 / 还原取 CornerRadius DP = 基类 ApplyMaximizedCorner；
        ///   · 溢出裁剪 = 基类根 Border Clip，内容不再反向撑大 HWND，无需钉高度。
        /// </summary>
        private void 切换最大化()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
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
        /// 按内容的实测高度设置窗口最小高度：窗口拖到最小也不会把内容裁掉。
        /// 内容高度 = 右侧栏三张卡片 + 表格 13 行 + 顶栏/页签/KPI/状态条（实测约 820 DIP）。
        /// 取 840 兜底（与旧版 DesiredSize 实测同口径）；工作区更矮时以工作区为准，
        /// 此时右栏与表格各自内部滚动，底部状态条始终可见。
        /// </summary>
        private void 按内容设置最小尺寸()
        {
            try
            {
                double 需要 = 840;
                double 上限 = SystemParameters.WorkArea.Height - 8;
                MinHeight = Math.Min(Math.Max(需要, 700), 上限);
            }
            catch { }
        }

        /// <summary>窗口尺寸变化：同步右侧栏宽度档位 + 钉住根内容高度</summary>
        private void 窗口_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            按窗口宽度调整右栏();
            钉住内容高度(e.NewSize.Height);
        }

        /// <summary>
        /// 把**根内容 Border** 的高度钉死成窗口客户区高度（本次 SizeChanged 的 NewSize，而不是
        /// 窗口的 ActualHeight）。
        /// 【V2.3.0 为什么不用 ActualHeight】MinHeight=840 时，若外部把窗口强改成更矮（截图
        /// harness / 远程桌面 / 系统显示缩放切换），ActualHeight 仍是 840，而可视区只有 780：
        /// 内容按 840 排版 → 底部状态条被裁到窗口外。NewSize 是这次变化的真实尺寸，永远与
        /// 可视区一致；常驻拖拽时两者相等，无副作用。
        /// 【V2.3.0 为什么还要钉】基类的根 Border Clip 只负责"裁掉溢出"，不做"压缩布局"：
        /// 不钉高度时，内容自然 DesiredSize（约 850+ DIP）大于窗口，底部状态条会被裁掉；
        /// 显式 Height 覆盖 DesiredSize 后，Grid 的 `*` 行被压缩 → DataGrid/右栏各自内部滚动，
        /// 底部状态条永远可见（旧版钉在「外框网格」上的同一口径，迁基类后钉在根 Border）。
        /// </summary>
        private void 钉住内容高度(double 目标)
        {
            try
            {
                目标 = Math.Max(0, 目标);
                // Height 缺省是 NaN（Auto），Math.Abs(NaN - 目标) 也是 NaN、比较恒 false，
                // 必须先判 NaN，否则永远钉不上（V2.3.0 迁移实测踩坑）。
                if (double.IsNaN(窗口内容.Height) || Math.Abs(窗口内容.Height - 目标) > 0.5)
                    窗口内容.Height = 目标;
            }
            catch { }
        }

        /// <summary>
        /// 窗口状态变化：更新最大化图标 + 最大化时去掉根 Border 的 12px 留白/描边。
        /// 根 Border 的圆角归零与裁剪由基类 ApplyMaximizedCorner 负责；这里处理基类不管的
        /// Padding/BorderThickness，以及【V2.3.1】新增的三块内容圆角（顶栏/页签带/状态条，
        /// 见 XAML 对应节点）：最大化铺满工作区时这三块若仍带 10px 圆角，屏幕四角会露出
        /// 缺口，必须与根 Border 同步归零；还原时再用 SetResourceReference 挂回库令牌
        /// （与 XAML 里的 DynamicResource 等价，主题切换仍能跟随）。
        /// </summary>
        private void 窗口_StateChanged(object sender, EventArgs e)
        {
            bool 最大化 = WindowState == WindowState.Maximized;
            if (窗口内容 != null)
            {
                窗口内容.Padding = new Thickness(最大化 ? 0 : 12);
                窗口内容.BorderThickness = new Thickness(最大化 ? 0 : 1);
            }
            // 内容块圆角随最大化归零 / 还原挂回库令牌（口径与根 Border 保持一致）
            if (最大化)
            {
                顶栏区.CornerRadius = new CornerRadius(0);
                页签区.CornerRadius = new CornerRadius(0);
                状态条区.CornerRadius = new CornerRadius(0);
            }
            else
            {
                顶栏区.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadiusTop");
                页签区.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadiusBottom");
                状态条区.SetResourceReference(Border.CornerRadiusProperty, "Fn.ShellRadius");
            }
            最大化图标.Data = 最大化
                ? Geometry.Parse("M5.5,5.5 H12.5 V12.5 H5.5 Z M3.5,10.5 V3.5 H10.5")
                : Geometry.Parse("M3.5,3.5 H10.5 V10.5 H3.5 Z");
        }

        /// <summary>关窗：停定时器 → 停采集 → 最后落一次盘</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            刷新定时器.Stop();
            if (采集 != null) 采集.停止();
            统计.保存全部();
            统计.库.关闭库();   // 【V2.3.1】退出前关 SQLite 连接，让 WAL 落回主库文件
            Log.Info("软件退出: 停定时器 → 停采集 → 最后落库 → 关库");
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
