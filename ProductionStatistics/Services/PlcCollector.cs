using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HslCommunication;
using HslCommunication.Profinet.Melsec;
using Kaleidoscope.Utils;

namespace ProductionStatistics
{
    /// <summary>
    /// PLC 采集服务（后台线程）：轮询三菱 FX5U，做上升沿计数与时间累计，把结果写进 StatisticsStore。
    ///
    /// 做什么（逻辑与 WinForms 版的 Form1.DataGet 完全一致，只是把"刷界面"的部分剥离成事件）：
    ///   ① 读 M3210/20/30 各 3 点（OK触发/NG触发/IDLE）+ M3200 起 2 点（自动中/报警），共 4 次报文；
    ///   ② 触发信号上升沿计数 → 计入当前小时桶；
    ///   ③ 按统计口径累计时间 → 计入当前小时桶；
    ///   ④ 运行日志：状态变化 / 每分钟"时间账目""本分钟产量" / 跨整点"小时结算" / 窄脉冲告警。
    ///
    /// 【时间记账的关键点 —— 保留 V2.1.0 的修复】
    ///   一轮循环的真实耗时 = 4 次报文往返 + 加锁 + 处理 + Thread.Sleep(轮询间隔)，
    ///   必然大于"名义轮询间隔 readInterval"（现场实测一轮约 330ms，而间隔只配 200ms）。
    ///   旧版本每轮固定记 readInterval/1000 秒，把中间那 100ms 整段漏掉了，
    ///   于是"整小时完全停机"只记到 39.4 分钟（正确应为 60.0 分钟）。
    ///   现在用 Stopwatch 量两次成功采样之间的真实间隔，按真实间隔计入时间账，
    ///   并且每分钟把"墙上经过多少秒 vs 实际计入多少秒"写进日志（时间账目），一眼可核对。
    ///
    /// 线程约定：采集线程只写内存 + 置脏标志，绝不碰界面（界面由 1 秒定时器消费事件刷新）。
    /// </summary>
    public class PlcCollector
    {
        // ===================== 对外事件 =====================
        /// <summary>统计数据有变化（UI 收到后置脏标志，由界面定时器统一刷新）</summary>
        public event Action 数据已变化;

        /// <summary>PLC 连接状态变化（true=已连接）</summary>
        public event Action<bool> 连接状态已变化;

        /// <summary>设备状态变化（文字描述：运行 / 停止 / 待料 / 报警）</summary>
        public event Action<string> 设备状态已变化;

        // ===================== PLC 通讯相关 =====================
        /// <summary>三菱 MC(3E帧) 通讯对象</summary>
        private MelsecMcNet melsecTcpNet;

        /// <summary>PLC 是否在线（采集线程写，UI 线程读；bool 读写原子，无需加锁）</summary>
        public bool IsPlcOnline { get; private set; }

        /// <summary>轮询间隔（毫秒），ini 可配，缺省 200；间隔越短边沿越不易漏，太快加重 PLC 负担</summary>
        private int readInterval = 200;

        /// <summary>
        /// 计数去抖：同一个触发位两次计数之间的最小间隔（秒）。
        /// ini 的 [PLC] 段 `计数最小间隔Ms`（默认 1500）可改；采集循环里还会自动抬高到不小于 2.5 倍采样周期。
        /// 用途：压住"自锁位复位后 PLC 很快重新置位"造成的同一件产品被重复计数（现场"计数一直加"的根因）。
        /// </summary>
        private double 最小计时间隔秒 = 1.5;

        /// <summary>
        /// 计数去抖：连续读到 OFF 持续多少毫秒才认为"信号真的结束了"（ini 的 信号确认OFF毫秒，默认 300）。
        /// V2.2.2 前为"轮数"口径（连续 2 轮 OFF），轮数×名义周期与真实时长偏差大（报文往返抖动），
        /// 会把真实的下一件也拦掉（漏计）——现按 Stopwatch 单调时钟量出的 OFF 持续时长确认。
        /// 现场实测复位空档约 230ms，默认 300ms = 空档 + 30% 余量。
        /// </summary>
        private int 确认OFF毫秒 = 300;

        // ===================== PLC 地址表（现场定义，改动需与用户确认） =====================
        /// <summary>三个单元名称</summary>
        public static readonly string[] 单元名称 = { "焊接单元", "气密单元", "贴标单元" };

        /// <summary>三个单元的英文名称（英文界面用）</summary>
        public static readonly string[] 单元名称En = { "Welding Unit", "Airtight Unit", "Labeling Unit" };

        /// <summary>
        /// PLC 信号地址配置：来自 生产统计.ini 的 [PLC地址] 段（现场可自行修改，改完重启软件生效）。
        /// 缺省值见《PLC地址定义.txt》：自动中 M3200、报警 M3201、三单元 OK/NG/IDLE = M3210~M3232。
        /// </summary>
        private readonly PlcAddressConfig 地址配置;

        /// <summary>自锁位复位器：把读到 true 的单元信号写回 false（设备自动中/报警是状态信号，不复位）</summary>
        private readonly PlcSignalResetter 复位器;

        /// <summary>三个单元的地址说明（界面右侧"单元状态"栏显示用，便于现场核对地址映射）</summary>
        public string 单元地址说明(int 单元号)
        {
            return 地址配置.单元OK[单元号] + " / " + 地址配置.单元NG[单元号] + " / " + 地址配置.单元IDLE[单元号];
        }

        /// <summary>
        /// 6 个产量触发信号的"名称+PLC地址"（下标与采集循环里的 unitTrigger 一一对应）。
        /// **按当前地址配置动态生成**：现场在 ini 里改了地址，日志里显示的也跟着对，
        /// 不会再出现"日志写 M3210、实际读的是 M3210"这种误导。
        /// </summary>
        public string[] 触发信号名
        {
            get
            {
                var 名 = new string[6];
                for (int i = 0; i < 3; i++)
                {
                    名[2 * i] = 单元名称[i] + "OK(" + 地址配置.单元OK[i] + ")";
                    名[2 * i + 1] = 单元名称[i] + "NG(" + 地址配置.单元NG[i] + ")";
                }
                return 名;
            }
        }

        // ===================== 统计数据 =====================
        /// <summary>统计仓库（采集线程写、UI 线程读，统一过仓库里的锁）</summary>
        private readonly StatisticsStore 统计;

        /// <summary>上一轮 6 个触发信号的状态（焊接OK/NG 气密OK/NG 贴标OK/NG），用于上升沿检测</summary>
        private bool[] lastTriggerStates = new bool[6];

        /// <summary>最近一轮读到的实时状态（3 个单元 IDLE、自动中、报警），采集线程写、UI 秒级读</summary>
        public bool[] latestUnitIdle = new bool[3];
        public bool latestAuto = false;
        public bool latestAlarm = false;

        /// <summary>数据脏标志：采集置 true，UI 定时器消费后置 false（高频采集与低频刷新解耦）</summary>
        public volatile bool dataDirty = true;

        /// <summary>采集线程（IsBackground 随进程退出）</summary>
        private Thread 采集线程;

        /// <summary>构造函数：传入统计仓库与 PLC 地址配置（地址来自 ini）</summary>
        public PlcCollector(StatisticsStore 统计仓库, PlcAddressConfig 地址)
        {
            统计 = 统计仓库;
            地址配置 = 地址;
            复位器 = new PlcSignalResetter(地址);
        }

        /// <summary>
        /// 按 ini 配置建立 PLC 连接并启动采集线程。
        /// 连接失败不抛异常（界面显示重试提示，采集线程下一轮自动重连）。
        /// </summary>
        public void 启动(string ip, int port, int 轮询间隔毫秒)
        {
            readInterval = Math.Max(50, 轮询间隔毫秒);
            最小计时间隔秒 = Math.Max(0.2, AppConfig.取整数("PLC", "计数最小间隔Ms", 1500) / 1000.0);
            确认OFF毫秒 = Math.Max(100, AppConfig.取整数("PLC", "信号确认OFF毫秒", 300));

            // 【V2.2.2 授权注册——"暂停一晚上第二天就不计数"的主根因修复】
            // HslCommunication 是商业授权组件，未注册授权时连接/读写会间歇性返回
            // "系统授权失败"（现场 11-18/11-19 日志实锤：整夜授权失败断线，
            // 断线时段的产量整段漏计，总数必然对不上单站）。
            // kaleidoscope.Utils.HslAuthorization 从 exe 同目录 hsl.dat 读 ENC1 加密授权码
            // 并调用 HSL 的 SetAuthorizationCode（本地验签，进程内幂等，可重复调用）。
            HslAuthorization.EnsureRegistered();
            Log.Info("HSL 授权状态: " + (HslAuthorization.IsActivated
                ? "已激活（hsl.dat 注册成功）"
                : "未激活！PLC 通讯会间歇性'系统授权失败'导致漏计，请把 hsl.dat 放到 exe 目录"));

            // 【V2.2.2 日志桥】kaleidoscope 库内部日志（HslPlcClient/连接监控等）统一接到本工程
            // 运行日志，现场排查通讯问题时一份日志看全。（LogAction 设置后库不再写自己的 Logs 目录）
            Kaleidoscope.Utils.LogHelper.LogAction = (level, message) =>
            {
                if (level == "ERROR") Log.Error("Kaleidoscope: " + message);
                else if (level == "WARN") Log.Warn("Kaleidoscope: " + message);
                else Log.Info("Kaleidoscope: " + message);
            };

            melsecTcpNet = new MelsecMcNet(ip, port);
            melsecTcpNet.ConnectTimeOut = 2000;   // 连接/读取超时 2 秒，PLC 断线时不至于长时间阻塞轮询
            melsecTcpNet.ReceiveTimeOut = 2000;

            Log.Info("读取配置: IP=" + ip + " 端口=" + port + " 轮询间隔=" + readInterval + "ms"
                + " | 合并读=" + (地址配置.可一次合并读()
                    ? "启用(起始 " + 地址配置.合并读起始地址() + " 数量 " + 地址配置.合并读数量() + "，1 次报文读回 11 位)"
                    : "未启用(地址跨度超窗口，走 4 次报文)"));
            Log.Info("PLC 地址配置: " + 地址配置.摘要());

            OperateResult connectResult = melsecTcpNet.ConnectServer();
            IsPlcOnline = connectResult.IsSuccess;
            if (connectResult.IsSuccess)
                Log.Info("PLC 连接成功: " + ip + ":" + port);
            else
                // 用 Warn 而不是 Error：连不上 PLC 是可自动恢复的常态（程序会一直重试），
                // 若记成 Error 会让"错误日志.txt"凭空出现，现场会误以为程序崩了
                Log.Warn("PLC 连接失败（程序会自动重试）: " + ip + ":" + port + " | " + connectResult.Message);

            采集线程 = new Thread(采集循环);
            采集线程.IsBackground = true;
            采集线程.Name = "采集线程"; // 给线程起名：日志里一眼看出哪几行是采集线程写的
            采集线程.Start();
            Log.Info("采集线程已启动");
        }

        /// <summary>停止采集（窗体关闭时调用）：断开 PLC 连接，采集线程靠 IsDisposed/停止标志自然退出</summary>
        public void 停止()
        {
            停止标志 = true;
            try { if (melsecTcpNet != null) melsecTcpNet.ConnectClose(); }
            catch { /* 竞态吞掉：进程即将退出 */ }
        }

        /// <summary>停止标志：窗体关闭时置位，采集循环下一轮自然退出</summary>
        private volatile bool 停止标志 = false;

        /// <summary>
        /// PLC 轮询线程（固定写法：while(true) + 停止标志退出 + Sleep 节流 + 双层 try-catch 兜底）。
        /// 每轮做四件事：①读信号（V2.2.2 起 11 个位合并成 1 次报文）；②上升沿计数；③按口径累计时间并置脏标志；④写运行日志。
        ///
        /// 【V2.2.2 通讯合并——漏计减半的治本手段】
        /// 老写法每轮 4 次报文（三单元各 1 次 + 自动中/报警 1 次），单轮周期实测 200~330ms，
        /// 触发位空档窗口跟着被拉宽，"复位-再置位"竞态漏计概率翻倍（日志实锤：复位重置位间隔 230ms ≈ 单轮周期 230ms）。
        /// 现在三个单元 OK/NG/IDLE + 自动中/报警共 11 个位，只要全部落在 M3200~M3299 这个 MC 协议可一次
        /// 点读的窗口内，就合并成一次 ReadBool(起始, 长度) 报文读回——单轮周期降到 ~120ms，漏采窗口减半。
        /// 地址不连续时自动回落老逻辑（4 次报文），现场自由配地址的能力不变。
        /// </summary>
        private void 采集循环()
        {
            // —— 采集线程专用的计时与计数：都放在方法内做局部变量，只有采集线程自己用，不存在跨线程共享问题 ——
            // 单调时钟：量真实经过时间，不受系统时间被改动（现场对时、时钟回拨）影响
            Stopwatch 采样时钟 = Stopwatch.StartNew();
            double 上次采样秒 = -1;       // 上一次成功采样时的时钟读数（秒）；-1 = 还没采过样，或刚发生过读取失败
            double 上轮循环结束秒 = -1;   // 上一轮循环结束时的时钟读数（秒），用来算单轮周期耗时
            double 上次汇总秒 = 采样时钟.Elapsed.TotalSeconds; // 上一份"时间账目"汇总的时刻（秒）
            long 采样次数 = 0;            // 本统计周期内的成功采样次数
            double 周期累计毫秒 = 0, 周期最大毫秒 = 0;      // 单轮周期耗时（毫秒）的累计 / 最大
            double 读耗时累计毫秒 = 0, 读耗时最大毫秒 = 0;  // 4 次报文读取耗时（毫秒）的累计 / 最大
            double 本期计入生产秒 = 0, 本期计入待料秒 = 0, 本期计入报警秒 = 0, 本期计入停止秒 = 0;
            long 本期离线次数 = 0;        // 本统计周期内的读取失败次数
            string 上次设备状态 = null;   // 上一次记录的设备状态（报警 / 运行 / 待料 / 停止）
            string 上次小时键 = null;     // 当前正在累计的小时桶标识（形如 2026-11-16夜班03）

            // —— 产量计数诊断用的计数器（现场反馈"实际生产了却没计数"，靠这几组数据定位原因）——
            bool 首次采样 = true;          // 第一次成功采样只作基准、不计数（见下面"首次采样"处的说明）
            int[] 连续On次数 = new int[6];  // 每个触发位连续被采到 ON 的次数（用来推算脉冲宽度）
            // —— 计数去抖（V2.2.0 修复"计数一直加"）——
            // 自锁位被我们复位后，PLC 重新置位前会有一个短暂 OFF 空档；
            // 老逻辑把这个 OFF 当成"上一件真的结束了"，下一轮又当成新的一件 → 同一件产品被反复计数。
            // 现在要求：连续读到 OFF 达到"确认OFF轮数"轮，才认为信号真的结束，之后的 ON 才计为新的一件。
            long[] 本分钟上升沿 = new long[6]; // 本分钟各触发位的上升沿次数（用于"计数速率异常"告警）
            // 边沿检测 + 去抖（共享类，可单测）：详见 TriggerDebouncer 的注释
            //（V2.2.2 起按"OFF 持续时长"确认，构造走毫秒签名；每轮循环里同步 ini 可改的确认OFF毫秒）
            var 去抖器 = new TriggerDebouncer(最小计时间隔秒, 确认OFF毫秒, true);
            double[] On开始秒 = new double[6]; // 每个触发位第一次被采到 ON 的时刻（用来算真实脉冲宽度）
            long[] 本分钟单元OK = new long[3]; // 本分钟各单元的 OK 计数增量
            long[] 本分钟单元NG = new long[3]; // 本分钟各单元的 NG 计数增量
            long 本分钟可疑脉冲 = 0;        // 本分钟"宽度接近采样周期"的可疑脉冲数（这些脉冲可能被漏检）

            // 单轮采样允许计入时间账的上限（秒）：超过说明电脑休眠 / 进程被挂起 / 长时间卡顿，
            // 这段时间设备状态未知，整段丢掉（宁可少记，也不能把一段假时间灌进当前小时桶）
            const double 最大计入间隔秒 = 10.0;

            try // 外层兜底：保线程不死；内层单轮隔离
            {
                while (true)
                {
                    if (停止标志) break;

                    // 定期落盘放在循环开头（而非读取成功之后）：PLC 离线时也照常存盘，
                    // 保证"数据\生产履历_日期.csv"始终跟进，现场随时拿走文件都是最新的
                    DateTime SaveNow = DateTime.Now;
                    if ((SaveNow - 统计.lastSaveTime).TotalSeconds >= 30)
                    {
                        统计.lastSaveTime = SaveNow;
                        统计.保存全部();
                    }

                    try
                    {
                        // ① 一轮读取：V2.2.2 起 11 个信号位优先合并成 1 次报文（读全部合并）；
                        //    地址不满足合并条件时回落 4 次报文的老路径，现场配地址自由度不变
                        long 读开始毫秒 = 采样时钟.ElapsedMilliseconds;
                        OperateResult<bool[]>[] 合并结果 = 读全部合并();
                        OperateResult<bool[]> r0, r1, r2, r3;
                        if (合并结果 != null)
                        {
                            r0 = 合并结果[0]; r1 = 合并结果[1]; r2 = 合并结果[2]; r3 = 合并结果[3];
                        }
                        else
                        {
                            r0 = 读单元(0);             // 焊接 OK/NG/IDLE
                            r1 = 读单元(1);             // 气密 OK/NG/IDLE
                            r2 = 读单元(2);             // 贴标 OK/NG/IDLE
                            r3 = 读自动与报警();        // 自动中/报警
                        }
                        double 读耗时毫秒 = 采样时钟.ElapsedMilliseconds - 读开始毫秒;

                        if (!r0.IsSuccess || !r1.IsSuccess || !r2.IsSuccess || !r3.IsSuccess)
                        {
                            // 失败原因取第一个失败报文的 Message
                            string 失败原因 = !r0.IsSuccess ? r0.Message : !r1.IsSuccess ? r1.Message
                                           : !r2.IsSuccess ? r2.Message : r3.Message;
                            设置连接状态(false, 失败原因);
                            本期离线次数++;
                            // 离线期间设备状态未知，这段时间不计入任何一类（否则会把"读不到"错记成"停止"）；
                            // 上次采样秒置 -1 = 重新开始计时，恢复连接后不会把离线那段时间一次性灌进某个小时桶
                            上次采样秒 = -1;
                            Thread.Sleep(readInterval);
                            continue;
                        }
                        设置连接状态(true, null);

                        // 展开成 6 个触发状态（0~5 依次：焊接OK/NG 气密OK/NG 贴标OK/NG），顺便存 IDLE 实时状态
                        bool[] unitTrigger = new bool[6];
                        // 单元信号展开成 9 位（0=焊接OK 1=焊接NG 2=焊接IDLE … 8=贴标IDLE），
                        // 供"自锁位复位"用：这 9 个点位读到 true 后要由软件写回 false
                        bool[] 单元信号 = new bool[9];
                        bool[][] reads = { r0.Content, r1.Content, r2.Content };
                        for (int i = 0; i < 3; i++)
                        {
                            unitTrigger[2 * i] = reads[i][0];      // OK触发
                            unitTrigger[2 * i + 1] = reads[i][1];  // NG触发
                            latestUnitIdle[i] = reads[i][2];       // 单元 IDLE（实时显示用）
                            单元信号[3 * i] = reads[i][0];
                            单元信号[3 * i + 1] = reads[i][1];
                            单元信号[3 * i + 2] = reads[i][2];
                        }
                        latestAuto = r3.Content[0];
                        latestAlarm = r3.Content[1];

                        // ② 计算当前时刻归属的班次与小时桶
                        //    白班 08:00-20:00；夜班 20:00-次日08:00，归属 20:00 所在日期（跨天桶不变）
                        DateTime NowTime = DateTime.Now;
                        bool isDayShift = NowTime.Hour >= 8 && NowTime.Hour < 20;
                        int hourIndex = isDayShift ? NowTime.Hour - 8
                                                   : (NowTime.Hour >= 20 ? NowTime.Hour - 20 : NowTime.Hour + 4);
                        DateTime ShiftDate = isDayShift ? NowTime.Date
                                            : (NowTime.Hour >= 20 ? NowTime.Date : NowTime.Date.AddDays(-1));

                        // ③ 真实经过时间：上一次成功采样 → 这一次成功采样，墙上真实过去了多少秒
                        //    （这是 V2.1.0 修复的核心：以前这里写死 readInterval/1000，把报文往返耗时整段漏掉了）
                        double 现在秒 = 采样时钟.Elapsed.TotalSeconds;
                        double 间隔秒 = 0;
                        if (上次采样秒 >= 0)
                        {
                            间隔秒 = 现在秒 - 上次采样秒;
                            if (间隔秒 > 最大计入间隔秒)
                            {
                                // 间隔异常大 = 电脑休眠 / 进程被挂起 / 长时间卡住，这段时间设备状态不可知，
                                // 整段丢掉（宁可少记也不能灌假时间进当前小时桶），并写日志留痕
                                Log.Warn("采样间隔异常 " + 间隔秒.ToString("F1", CultureInfo.InvariantCulture)
                                       + " 秒（上限 " + 最大计入间隔秒 + " 秒），该段不计入时间统计");
                                间隔秒 = 0;
                            }
                        }
                        上次采样秒 = 现在秒;
                        // 本轮采样周期（毫秒）：用于判断"脉冲宽度是否窄到可能被漏检"，取实测值与配置值的较大者
                        double 采样周期毫秒 = Math.Max(间隔秒 * 1000.0, readInterval);
                        // 去抖的最小计时间隔不能小于 2.5 倍采样周期（否则复位造成的快速重复信号压不住）
                        //【V2.2.2】确认OFF门槛同步换算成秒（TriggerDebouncer 已改为按 OFF 持续时长确认）
                        去抖器.最小计时间隔秒 = Math.Max(最小计时间隔秒, 2.5 * 采样周期毫秒 / 1000.0);
                        去抖器.确认OFF毫秒 = 确认OFF毫秒;

                        // ④ 上升沿计数 + 时间累计（过锁；锁内只有内存操作，无 IO）
                        lock (统计.锁)
                        {
                            DayRecord rec = 统计.取记录(ShiftDate);
                            HourStat hs = isDayShift ? rec.DayShift[hourIndex] : rec.NightShift[hourIndex];

                            // 产量计数：信号 on 且上轮 off 才计 1 次（上升沿），防止触发信号保持 on 被重复计数
                            if (首次采样)
                            {
                                // 【V2.1.0 修复"生产了没计数"的一种成因】
                                // 第一次成功采样只作"基准"：把当前状态同步进 lastTriggerStates，不计产量。
                                // 原因：若 PLC 的触发位是"置位后保持"的自锁位，程序启动时它可能还残留着上一件产品的 ON，
                                //       老写法会把它当成一次新生产记 1，之后因为一直 ON 就再也计不到数（看起来就是"一直在生产却永远不计数"）。
                                首次采样 = false;
                                Array.Copy(unitTrigger, lastTriggerStates, 6);
                                for (int i = 0; i < 6; i++)
                                {
                                    去抖器.基准同步(i, unitTrigger[i], 现在秒);
                                    if (unitTrigger[i]) { 连续On次数[i] = 1; On开始秒[i] = 现在秒; }
                                }
                                Log.Info("首次采样完成（作为计数基准，本次不计产量）: " + 触发位串(unitTrigger));
                            }
                            else
                            {
                                for (int i = 0; i < 6; i++)
                                {
                                    bool 上一轮ON = 去抖器.上一轮是ON(i);
                                    bool 是新脉冲 = 去抖器.喂入(i, unitTrigger[i], 现在秒);
                                    if (unitTrigger[i])
                                    {
                                        // 持续 ON：记连续次数与首次 ON 的时刻，用来推算这个脉冲有多宽
                                        连续On次数[i]++;
                                        if (连续On次数[i] == 1) On开始秒[i] = 现在秒;
                                        if (是新脉冲)
                                        {
                                            // 上升沿：计 1 次产量。
                                            // 【去抖】TriggerDebouncer 已确认"信号真的结束过"才算新的一件：
                                            //      自锁位被复位后 PLC 重新置位前会有一个短暂 OFF 空档，
                                            //      把它当成"上一件结束"就会重复计数（现场"计数一直加"的根因）。
                                            if (i % 2 == 0) { hs.AddOk(i / 2); 本分钟单元OK[i / 2]++; }
                                            else { hs.AddNg(i / 2); 本分钟单元NG[i / 2]++; }
                                            //【V2.2.2 修复】老代码这里漏了累加，"计数速率异常"告警从未触发过
                                            本分钟上升沿[i]++;
                                        }
                                    }
                                    else
                                    {
                                        if (上一轮ON && 连续On次数[i] > 0)
                                        {
                                            // 下降沿：脉冲结束，算它的宽度
                                            // 可疑判据（满足其一就算"脉冲可能太窄、被整段漏掉"）：
                                            //   ①整个脉冲只被采样到 1 次：它可能整个落在两次采样之间，一次都没被看见
                                            //   ②宽度不足 1.5 倍采样周期：采样相位一变就可能只抓到一次甚至抓不到
                                            //（现场"生产了没计数"的最常见原因：PLC 触发脉冲宽度 < 软件实际采样周期）
                                            double 宽度毫秒 = (现在秒 - On开始秒[i]) * 1000.0;
                                            if (连续On次数[i] <= 1 || 宽度毫秒 < 1.5 * 采样周期毫秒)
                                            {
                                                本分钟可疑脉冲++;
                                                string 该地址 = 复位器.触发地址(i);
                                                if (复位器.需要复位(该地址))
                                                {
                                                    // 自锁位（软件复位它）：复位后 PLC 会重新置位，看到的"宽度"天然≈采样周期，
                                                    // 真正的风险是"PLC 生产节拍快于采样周期" → 两件产品合并成一次，产量漏计。
                                                    // 这类提示降频（5 分钟一条），免得刷屏。
                                                    Log.Warn节流("自锁位过密:" + 触发信号名[i],
                                                        "自锁位 " + 触发信号名[i] + " 复位后重新置位间隔约 "
                                                        + 宽度毫秒.ToString("F0", CultureInfo.InvariantCulture) + "ms，接近单轮采样周期 "
                                                        + 采样周期毫秒.ToString("F0", CultureInfo.InvariantCulture)
                                                        + "ms：若 PLC 生产节拍快于采样周期，两件产品会合并成一次而漏计产量，"
                                                        + "建议把 生产统计.ini 的 轮询间隔Ms 调小（当前 " + readInterval + "ms）", 300);
                                                }
                                                else
                                                {
                                                    Log.Warn节流("脉冲过窄:" + 触发信号名[i],
                                                        "触发位 " + 触发信号名[i] + " 捕获到的脉冲宽度约 "
                                                        + 宽度毫秒.ToString("F0", CultureInfo.InvariantCulture) + "ms（只被采样到 "
                                                        + 连续On次数[i] + " 次），单轮采样周期约 "
                                                        + 采样周期毫秒.ToString("F0", CultureInfo.InvariantCulture)
                                                        + "ms：脉冲可能太窄而被整段漏掉（漏计产量）。建议：①把 PLC 该触发位改成置位保持型；"
                                                        + "②或把 生产统计.ini 里的 轮询间隔Ms 调小（当前 " + readInterval + "ms）", 60);
                                                }
                                            }
                                        }
                                        连续On次数[i] = 0;
                                    }
                                }
                            }

                            // 时间归类（口径优先级：报警 > 生产 > 待料 > 停止）
                            // 四类互斥且完全覆盖：每一段真实时间只会落进其中一类，
                            // 所以"四类之和"应当等于"墙上经过的时间"——这条等式就是本方法的验收标准
                            if (latestAlarm) { hs.AlarmSec += 间隔秒; 本期计入报警秒 += 间隔秒; }
                            else if (latestAuto) { hs.ProductionSec += 间隔秒; 本期计入生产秒 += 间隔秒; }
                            else if (latestUnitIdle[0] || latestUnitIdle[1] || latestUnitIdle[2]) { hs.WaitSec += 间隔秒; 本期计入待料秒 += 间隔秒; }
                            else { hs.StopSec += 间隔秒; 本期计入停止秒 += 间隔秒; }
                        }
                        dataDirty = true;

                        // 自锁位复位：读到 true 的单元信号写回 false（设备自动中 / 设备报警是状态信号，不复位）
                        // 放在锁外做：锁内禁止网络 IO；一次写只要几毫秒，赶在下一轮采样之前完成。
                        // 首次采样也会走到这里：残留的 ON 必须清掉，否则会挡住下一件产品的上升沿。
                        var 复位失败 = 复位器.复位单元信号(melsecTcpNet, 单元信号);
                        if (复位失败.Count > 0)
                            Log.Warn("PLC 信号复位失败（可能造成重复计数或漏计）: " + string.Join("  ", 复位失败));
                        if (数据已变化 != null) 数据已变化();

                        // ⑤ 运行日志：设备状态变化（同一状态 5 秒内只记一条，防止信号抖动刷屏）
                        string 当前状态 = 状态描述(latestAlarm, latestAuto, latestUnitIdle);
                        if (当前状态 != 上次设备状态)
                        {
                            Log.Info节流("设备状态变化",
                                "设备状态: " + (上次设备状态 ?? "初始") + " → " + 当前状态
                                + "（自动中=" + (latestAuto ? "ON" : "OFF")
                                + " 报警=" + (latestAlarm ? "ON" : "OFF")
                                + " 焊接IDLE=" + (latestUnitIdle[0] ? "ON" : "OFF")
                                + " 气密IDLE=" + (latestUnitIdle[1] ? "ON" : "OFF")
                                + " 贴标IDLE=" + (latestUnitIdle[2] ? "ON" : "OFF") + "）", 5);
                            上次设备状态 = 当前状态;
                            if (设备状态已变化 != null) 设备状态已变化(当前状态);
                        }

                        // ⑥ 运行日志：跨整点时结算上一小时（现场核对"这一小时到底停了多少分钟"最直观）
                        string 小时键 = ShiftDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                      + (isDayShift ? "白班" : "夜班") + hourIndex.ToString("00", CultureInfo.InvariantCulture);
                        if (上次小时键 == null) 上次小时键 = 小时键;
                        else if (上次小时键 != 小时键)
                        {
                            记录小时结算(上次小时键);
                            上次小时键 = 小时键;
                        }

                        // ⑦ 运行日志：每分钟一行"时间账目"（墙上经过 vs 实际计入 + 采样/报文耗时统计）
                        采样次数++;
                        if (上轮循环结束秒 >= 0)
                        {
                            double 周期毫秒 = (现在秒 - 上轮循环结束秒) * 1000.0;
                            周期累计毫秒 += 周期毫秒;
                            if (周期毫秒 > 周期最大毫秒) 周期最大毫秒 = 周期毫秒;
                        }
                        读耗时累计毫秒 += 读耗时毫秒;
                        if (读耗时毫秒 > 读耗时最大毫秒) 读耗时最大毫秒 = 读耗时毫秒;

                        if (现在秒 - 上次汇总秒 >= 60)
                        {
                            double 墙上秒 = 现在秒 - 上次汇总秒;
                            double 计入秒 = 本期计入生产秒 + 本期计入待料秒 + 本期计入报警秒 + 本期计入停止秒;
                            Log.Info(string.Format(CultureInfo.InvariantCulture,
                                "时间账目 采样={0}次 单轮周期avg={1:F0}ms/max={2:F0}ms 报文读取avg={3:F0}ms/max={4:F0}ms 合并读={5} | 墙上经过={6:F1}秒 计入={7:F1}秒（生产={8:F1} 待料={9:F1} 报警={10:F1} 停止={11:F1}）差额={12:F1}秒 离线={13}次",
                                采样次数,
                                采样次数 > 0 ? 周期累计毫秒 / 采样次数 : 0, 周期最大毫秒,
                                采样次数 > 0 ? 读耗时累计毫秒 / 采样次数 : 0, 读耗时最大毫秒,
                                合并读生效 ? 1 : 0,
                                墙上秒, 计入秒,
                                本期计入生产秒, 本期计入待料秒, 本期计入报警秒, 本期计入停止秒,
                                墙上秒 - 计入秒, 本期离线次数));

                            // 本分钟产量小结：现场一眼就能看出是哪个单元没计数
                            long 本分钟OK = 本分钟单元OK[0] + 本分钟单元OK[1] + 本分钟单元OK[2];
                            long 本分钟NG = 本分钟单元NG[0] + 本分钟单元NG[1] + 本分钟单元NG[2];
                            Log.Info(string.Format(CultureInfo.InvariantCulture,
                                "本分钟产量 焊接OK={0} NG={1} | 气密OK={2} NG={3} | 贴标OK={4} NG={5} | 合计={6} | 可疑窄脉冲={7} | 信号 自动中={8} 报警={9} 焊接IDLE={10} 气密IDLE={11} 贴标IDLE={12}",
                                本分钟单元OK[0], 本分钟单元NG[0], 本分钟单元OK[1], 本分钟单元NG[1],
                                本分钟单元OK[2], 本分钟单元NG[2], 本分钟OK + 本分钟NG, 本分钟可疑脉冲,
                                latestAuto ? 1 : 0, latestAlarm ? 1 : 0,
                                latestUnitIdle[0] ? 1 : 0, latestUnitIdle[1] ? 1 : 0, latestUnitIdle[2] ? 1 : 0));

                            // "在生产却没计数"告警：设备运行了半分钟以上，三个单元却一个都没计到
                            if (本期计入生产秒 >= 30 && 本分钟OK + 本分钟NG == 0)
                                Log.Warn("设备本分钟处于运行状态（生产时间 " + 本期计入生产秒.ToString("F0", CultureInfo.InvariantCulture)
                                    + "秒），但三个单元都没有计到任何 OK/NG。请检查：①触发信号地址/接线是否正确（对照上一行信号值）；"
                                    + "②触发脉冲是否太窄被漏采（看有没有'脉冲宽度'告警）；③PLC 触发位是否为置位保持型");

                            // 计数速率异常告警：某触发位一分钟内上升沿过多 → 多半是地址配错或信号抖动
                            //（V2.2.2 修复：老代码漏了累加语句，本告警从未触发过——现在在上升沿计数处同步累加，
                            //  位置在下面"④ 上升沿计数"的 是新脉冲 分支里）
                            for (int i = 0; i < 6; i++)
                            {
                                if (本分钟上升沿[i] >= 60)
                                    Log.Warn("触发位 " + 触发信号名[i] + " 本分钟上升沿 " + 本分钟上升沿[i]
                                        + " 次，明显快于正常生产节拍：请核对该地址是否真的是产量触发位"
                                        + "（改 生产统计.ini 的 [PLC地址] 段；日志里的『首次采样完成』一行能看到各点位实时状态）");
                                本分钟上升沿[i] = 0;
                            }

                            // 计数器清零，进入下一个统计周期
                            采样次数 = 0; 周期累计毫秒 = 0; 周期最大毫秒 = 0;
                            读耗时累计毫秒 = 0; 读耗时最大毫秒 = 0;
                            本期计入生产秒 = 0; 本期计入待料秒 = 0; 本期计入报警秒 = 0; 本期计入停止秒 = 0;
                            本期离线次数 = 0;
                            本分钟单元OK[0] = 本分钟单元OK[1] = 本分钟单元OK[2] = 0;
                            本分钟单元NG[0] = 本分钟单元NG[1] = 本分钟单元NG[2] = 0;
                            本分钟可疑脉冲 = 0;
                            上次汇总秒 = 现在秒;
                        }
                    }
                    catch (Exception exInner)
                    {
                        // 内层单轮隔离：本轮失败（网络抖动等）标记离线，下一轮继续，不许线程死
                        设置连接状态(false, exInner.Message);
                    }
                    // 记下本轮结束时刻（放在 Sleep 之前，等价于"下一轮开始时刻"，下一轮用它算单轮周期耗时）
                    上轮循环结束秒 = 采样时钟.Elapsed.TotalSeconds;
                    Thread.Sleep(readInterval);
                }
            }
            catch (Exception exOuter)
            {
                // 外层兜底：理论到不了这里（while 内已被内层接住），保线程万一崩溃不再抛，但一定要留痕
                Log.Error("采集线程异常退出", exOuter);
            }
        }

        /// <summary>
        /// 读一个单元的 OK/NG/IDLE 三个位（地址来自 ini 的 [PLC地址] 段）。
        /// 三个地址数值连续时（例：M3210/M3211/M3212）一次 ReadBool(起始,3) 读完，省报文；
        /// 不连续时（例：M3210/M3015/M3220）分三次单点读，保证现场可以自由配地址。
        /// </summary>
        private OperateResult<bool[]> 读单元(int 单元号)
        {
            if (地址配置.单元地址连续(单元号))
                return melsecTcpNet.ReadBool(地址配置.单元OK[单元号], 3);

            var rOk = melsecTcpNet.ReadBool(地址配置.单元OK[单元号], 1);
            if (!rOk.IsSuccess) return rOk;
            var rNg = melsecTcpNet.ReadBool(地址配置.单元NG[单元号], 1);
            if (!rNg.IsSuccess) return rNg;
            var rIdle = melsecTcpNet.ReadBool(地址配置.单元IDLE[单元号], 1);
            if (!rIdle.IsSuccess) return rIdle;

            return OperateResult.CreateSuccessResult(new[] { rOk.Content[0], rNg.Content[0], rIdle.Content[0] });
        }

        /// <summary>
        /// 读"设备自动中 + 设备报警"两个位（地址来自 ini 的 [PLC地址] 段）。
        /// 两个地址数值连续时（例：M3200/M3201）一次读完；不连续时分两次单点读。
        /// </summary>
        private OperateResult<bool[]> 读自动与报警()
        {
            if (地址配置.自动中报警连续())
                return melsecTcpNet.ReadBool(地址配置.设备自动中, 2);

            var rAuto = melsecTcpNet.ReadBool(地址配置.设备自动中, 1);
            if (!rAuto.IsSuccess) return rAuto;
            var rAlarm = melsecTcpNet.ReadBool(地址配置.设备报警, 1);
            if (!rAlarm.IsSuccess) return rAlarm;

            return OperateResult.CreateSuccessResult(new[] { rAuto.Content[0], rAlarm.Content[0] });
        }

        /// <summary>
        /// 【V2.2.2 新增】11 个信号位合并读：把"自动中/报警 + 三单元 OK/NG/IDLE"共 11 个地址
        /// 一次性 ReadBool(起始, 跨度) 读回，拆成老接口的 4 段结果——调用方代码零改动。
        /// </summary>
        /// <returns>null = 地址不满足合并条件或本次报文失败（调用方回落老路径）；非 null = 4 段读取结果</returns>
        private OperateResult<bool[]>[] 读全部合并()
        {
            if (!地址配置.可一次合并读()) return null;
            try
            {
                OperateResult<bool[]> 全段 = melsecTcpNet.ReadBool(
                    地址配置.合并读起始地址(), 地址配置.合并读数量());
                if (!全段.IsSuccess) return null;
                int[] 偏移 = 地址配置.合并读偏移表();

                // 按老接口的 4 段结构拆包：r3(自动中/报警) → r0~r2(三单元 OK/NG/IDLE)
                var r3 = OperateResult.CreateSuccessResult(new[] { 全段.Content[偏移[0]], 全段.Content[偏移[1]] });
                var 结果 = new OperateResult<bool[]>[4];
                结果[3] = r3;
                for (int u = 0; u < 3; u++)
                {
                    int o = 2 + 3 * u;
                    结果[u] = OperateResult.CreateSuccessResult(new[]
                        { 全段.Content[偏移[o]], 全段.Content[偏移[o + 1]], 全段.Content[偏移[o + 2]] });
                }
                合并读生效 = true;
                return 结果;
            }
            catch (Exception ex)
            {
                // 合并读任何异常都不该影响采集：记一次日志并回落老路径（下一轮重试）
                Log.Warn("合并读异常（本轮回落逐点读）: " + ex.Message);
                return null;
            }
        }

        /// <summary>合并读是否生效（启动后第一条"时间账目"日志里体现，便于现场确认 1 次报文模式在跑）</summary>
        private bool 合并读生效 = false;

        /// <summary>
        /// 跨整点时把刚结束的那一小时结算成一行日志（现场核对"这一小时到底停了多少分钟"最直观）。
        /// 小时键格式：yyyy-MM-dd + 白班/夜班 + 两位小时下标，例如 2026-11-16夜班03。
        /// 结算用的是内存里的累计值，与界面/CSV 显示的是同一份数据，三处口径天然一致。
        /// </summary>
        private void 记录小时结算(string 小时键)
        {
            try
            {
                DateTime 日期 = DateTime.ParseExact(小时键.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                bool 是白班 = 小时键.Substring(10, 2) == "白班";
                int 下标 = int.Parse(小时键.Substring(12, 2), CultureInfo.InvariantCulture);
                string 文字;
                lock (统计.锁)
                {
                    DayRecord rec = 统计.取记录(日期);
                    HourStat hs = 是白班 ? rec.DayShift[下标] : rec.NightShift[下标];
                    文字 = string.Format(CultureInfo.InvariantCulture,
                        "小时结算 {0}: OK={1} NG={2} | 生产={3:F1}分 待料={4:F1}分 报警={5:F1}分 停止={6:F1}分 四类合计={7:F1}分",
                        小时键, hs.OkCount, hs.NgCount,
                        hs.ProductionSec / 60.0, hs.WaitSec / 60.0, hs.AlarmSec / 60.0, hs.StopSec / 60.0,
                        (hs.ProductionSec + hs.WaitSec + hs.AlarmSec + hs.StopSec) / 60.0);
                }
                Log.Info(文字);
            }
            catch (Exception ex)
            {
                Log.Error("小时结算日志失败", ex);
            }
        }

        /// <summary>切换 PLC 在线状态（状态变化才记日志 + 通知界面）</summary>
        private void 设置连接状态(bool 在线, string error)
        {
            if (IsPlcOnline == 在线)
            {
                // 状态没变化：若仍处于离线，每 60 秒记一条"还在重试"，现场看日志就知道一直没连上
                if (!在线) Log.Warn节流("PLC仍未连上", "PLC 仍未连上（继续重试中）: " + error, 60);
                return;
            }
            IsPlcOnline = 在线;
            // 连接状态变化写日志：现场最常问的就是"昨晚到底断没断线"，这里留下确切时间点
            if (在线) Log.Info("PLC 已恢复连接");
            else Log.Warn("PLC 连接断开: " + error);
            if (连接状态已变化 != null) 连接状态已变化(在线);
        }

        /// <summary>
        /// 把当前信号翻译成状态文字（与统计口径同一优先级：报警 &gt; 运行 &gt; 待料 &gt; 停止）。
        /// 只用于日志与界面显示，不参与统计计算。
        /// </summary>
        public static string 状态描述(bool 报警, bool 自动中, bool[] 单元空闲)
        {
            if (报警) return "报警";
            if (自动中) return "运行";
            if (单元空闲[0] || 单元空闲[1] || 单元空闲[2]) return "待料";
            return "停止";
        }

        /// <summary>
        /// 把 6 个产量触发位的当前状态拼成一行文字（写日志用）。
        /// 现场拿日志核对时，一眼能看出"哪个地址现在是 ON"，配合 PLC 侧观察即可确认地址映射对不对。
        /// </summary>
        public string 触发位串(bool[] unitTrigger)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 6; i++)
                sb.Append(触发信号名[i]).Append("=").Append(unitTrigger[i] ? "1" : "0").Append("  ");
            return sb.ToString();
        }
    }
}
