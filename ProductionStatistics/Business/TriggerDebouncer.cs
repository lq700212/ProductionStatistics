using System;

namespace ProductionStatistics
{
    /// <summary>
    /// 产量触发信号的"边沿检测 + 去抖"（WinForms 与 WPF 两版共用同一份源文件）。
    ///
    /// 做什么：把每一轮采样到的 6 个触发位（焊接OK/NG、气密OK/NG、贴标OK/NG）转成
    ///   "这一位这一轮是不是**新的一件产品**"的判断，供计数使用。
    ///
    /// 为什么需要去抖（现场"计数一直加"的根因）：
    ///   现场触发位是**自锁位**（置位后保持 ON），软件读到 ON 后会写回 false 复位。
    ///   如果 PLC 在复位后很快（短于一个采样周期）就重新置位（同一件产品还在），
    ///   软件有概率正好采到那个"短暂 OFF 空档"，老逻辑就会把它当成"上一件真的结束了"，
    ///   下一轮又当成新的一件 → **同一件产品被反复计数，产量飞涨**。
    ///
    /// 去抖办法（两个条件同时满足才算"新的一件"）：
    ///   ① **确认过 OFF**：连续读到 OFF **持续满"确认OFF毫秒"毫秒**，才认为信号真的结束过
    ///      （V2.2.2 前按"连续 OFF 轮数"确认——轮数 × 名义周期 ≠ 真实时间：报文往返抖动会让
    ///      "2 轮 OFF"的实际持续时长在 300~700ms 间漂移，把真实的下一件也拦掉（漏计）。
    ///      改成按 Stopwatch 单调时钟量出的 OFF 持续时长确认，时间语义精确，不受周期抖动影响；
    ///   ② **距上次计数够久**：两次计数至少间隔"最小计时间隔"
    ///      （挡住"两个真实脉冲挨得太近"被误合并）。
    ///   另外：首次采样只作基准、**不计数**（启动时残留的 ON 不算新的一件），
    ///   但若启动时信号是 OFF，则下一件可以立刻计（不漏第一件）。
    ///
    /// 取值：确认OFF毫秒默认 300（≈现场实测复位空档 230ms + 30% 余量，ini 的 `信号确认OFF毫秒` 可调），
    ///   最小计时间隔默认 1500ms（ini 的 `计数最小间隔Ms`）。两者都应大于"复位空档时长"，
    ///   又要小于现场真实生产节拍（正常几秒一件）。
    /// </summary>
    public class TriggerDebouncer
    {
        /// <summary>触发位个数：焊接OK/NG、气密OK/NG、贴标OK/NG</summary>
        public const int 信号数 = 6;

        /// <summary>上一轮采样到的状态（true=ON）</summary>
        private readonly bool[] 上一轮值 = new bool[信号数];

        /// <summary>是否已确认"信号真的结束过"（确认过之后，下一个 ON 才算新的一件）</summary>
        private readonly bool[] 已确认OFF = new bool[信号数];

        /// <summary>本轮 OFF 起始时刻（秒，单调时钟）；ON 时无意义</summary>
        private readonly double[] OFF开始秒 = new double[信号数];

        /// <summary>上一次计数的时刻（秒，单调时钟）</summary>
        private readonly double[] 上次计数秒 = new double[信号数];

        /// <summary>
        /// 最小计时间隔（秒）：两次计数之间至少要隔这么久。
        /// 采集循环里会按实际采样周期自动抬高（不小于 2.5 倍采样周期），避免配得太小失去去抖作用。
        /// </summary>
        public double 最小计时间隔秒 { get; set; }

        /// <summary>
        /// 连续读到 OFF 持续多少毫秒才认为信号真的结束（V2.2.2 前为"轮数"口径，实测轮数×周期漂移大）。
        /// </summary>
        public int 确认OFF毫秒 { get; set; }

        /// <summary>构造函数（旧签名兼容：轮数 × 200ms 估算毫秒——仅供旧调用方过渡，业务代码一律用新签名）</summary>
        public TriggerDebouncer(double 最小计时间隔秒, int 确认OFF轮数)
        {
            this.最小计时间隔秒 = 最小计时间隔秒 < 0 ? 0 : 最小计时间隔秒;
            this.确认OFF毫秒 = Math.Max(100, 确认OFF轮数 * 200);
        }

        /// <summary>构造函数：确认OFF毫秒 = OFF 持续时长的确认门槛（毫秒）</summary>
        public TriggerDebouncer(double 最小计时间隔秒, int 确认OFF毫秒, bool 按毫秒)
        {
            this.最小计时间隔秒 = 最小计时间隔秒 < 0 ? 0 : 最小计时间隔秒;
            this.确认OFF毫秒 = Math.Max(100, 确认OFF毫秒);
        }

        /// <summary>喂入本轮采样值之前，先取该位上一轮的值（判断下降沿时用）</summary>
        public bool 上一轮是ON(int i)
        {
            return 上一轮值[i];
        }

        /// <summary>
        /// 喂入本轮采样值，返回"这一位这一轮是不是新的一件产品"（该计产量了）。
        /// </summary>
        public bool 喂入(int i, bool 当前ON, double 现在秒)
        {
            bool 是新脉冲 = false;
            if (当前ON)
            {
                // 又看到 ON：先算刚才这个 OFF 空档持续了多久
                if (!上一轮值[i] && OFF开始秒[i] > 0)
                {
                    double OFF时长毫秒 = (现在秒 - OFF开始秒[i]) * 1000.0;
                    // OFF 持续满确认门槛 → 信号真的结束过，下一个 ON 才算新的一件
                    if (OFF时长毫秒 >= 确认OFF毫秒) 已确认OFF[i] = true;
                }
                // 上升沿 + 已确认信号真的结束过 + 距上次计数够久 → 新的一件
                if (!上一轮值[i] && 已确认OFF[i] && (现在秒 - 上次计数秒[i]) >= 最小计时间隔秒)
                {
                    是新脉冲 = true;
                    上次计数秒[i] = 现在秒;
                }
                OFF开始秒[i] = 0;   // 回到 ON：OFF 计时复位
            }
            else
            {
                // OFF：记下本轮 OFF 的起始时刻（连续 OFF 期间只在第一个 OFF 轮记一次）
                if (上一轮值[i] || OFF开始秒[i] <= 0) OFF开始秒[i] = 现在秒;
            }
            上一轮值[i] = 当前ON;
            return 是新脉冲;
        }

        /// <summary>
        /// 首次采样：只作基准、**不计数**，按当前值初始化去抖状态。
        /// 启动时是 OFF 的位 → 下一件可以立刻计；启动时是 ON 的位（上一件的残留）→ 视为"刚发生过"，
        /// 要等它真的结束才计下一件（宁可少计这一件，也绝不多计）。
        /// </summary>
        public void 基准同步(int i, bool 当前ON, double 现在秒)
        {
            上一轮值[i] = 当前ON;
            已确认OFF[i] = !当前ON;                        // ON → 等它真的结束；OFF → 下一件可以计
            OFF开始秒[i] = 当前ON ? 0 : 现在秒;            // OFF 时记下起点（供后续持续时长计算）
            上次计数秒[i] = 当前ON ? 现在秒 : double.NegativeInfinity;  // OFF → 允许下一件立即计
        }
    }
}
