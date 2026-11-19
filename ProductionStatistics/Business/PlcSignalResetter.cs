using System;
using System.Collections.Generic;
using System.Text;
using HslCommunication;
using HslCommunication.Profinet.Melsec;

namespace ProductionStatistics
{
    /// <summary>
    /// 产量/状态信号的"自锁位复位"工具（WinForms 与 WPF 两版共用同一份源文件）。
    ///
    /// 现场 PLC 的点位是**自锁位**（置位后保持 ON，不会自己清零），因此：
    ///   · 三个单元的 OK / NG / IDLE 触发位：读到 true 后**必须由软件写回 false**。
    ///     否则它会一直保持 ON —— ①上升沿再也不会出现（产量永远只记第一次，这就是现场"生产了却没计数"的成因）；
    ///     ②IDLE 一直 ON 会让"待料时间"无限累计。
    ///   · 设备自动中(M3200) 与 设备报警(M3201)：是**状态信号**，读到 true **不复位**，保持现状。
    ///
    /// 复位时机：每轮采样读到 true 之后立即写 false。
    ///   · 放在**数据锁之外**做（锁内禁止网络 IO），写一次只要几毫秒，赶在下一轮采样之前完成。
    ///   · **首次采样也要复位**：首次采样只作计数基准、不计数，但残留的 ON 必须清掉，
    ///     否则它会挡住下一件产品的上升沿，后面就永远计不到数了。
    ///
    /// 例外处理：若某个信号其实是 PLC 持续维护的状态（而不是自锁位），软件复位后 PLC 会立刻再置 true，
    ///   那样每轮都会多写一次报文（通讯负担上升）。这种地址可以在 ini 的 `[PLC地址]` 段里
    ///   用 `不复位地址=M3212,M3232` 排除（逗号分隔，可多个）。
    /// </summary>
    public class PlcSignalResetter
    {
        /// <summary>每个单元的信号个数：OK / NG / IDLE</summary>
        public const int 每单元信号数 = 3;

        /// <summary>单元数：焊接 / 气密 / 贴标</summary>
        public const int 单元数 = 3;

        /// <summary>地址配置（决定每个信号读哪个点位）</summary>
        private readonly PlcAddressConfig 地址配置;

        /// <summary>不复位的地址集合（比较时忽略大小写）：默认只有 设备自动中 与 设备报警</summary>
        private readonly HashSet<string> 不复位集合 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>构造函数：传入地址配置</summary>
        public PlcSignalResetter(PlcAddressConfig 配置)
        {
            地址配置 = 配置;
            // 状态信号默认不复位（现场明确要求：自动中/报警读到 true 后保持不动）
            不复位集合.Add(配置.设备自动中);
            不复位集合.Add(配置.设备报警);
            // ini 里额外配置的"不复位地址"（逗号/分号/空格分隔）
            foreach (string 项 in (配置.不复位地址 ?? "").Split(
                         new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                不复位集合.Add(项.Trim());
        }

        /// <summary>这个地址要不要在读到 true 后写回 false</summary>
        public bool 需要复位(string 地址)
        {
            if (string.IsNullOrEmpty(地址)) return false;
            return !不复位集合.Contains(地址.Trim());
        }

        /// <summary>
        /// 把"读到 true"的单元信号写回 false。
        /// 单元信号数组顺序（9 位）：0=焊接OK 1=焊接NG 2=焊接IDLE 3=气密OK … 8=贴标IDLE。
        /// 返回写失败的地址列表（空列表 = 全部成功）。
        /// </summary>
        public List<string> 复位单元信号(MelsecMcNet 网关, bool[] 单元信号)
        {
            var 失败 = new List<string>();
            if (网关 == null || 单元信号 == null) return 失败;
            for (int i = 0; i < 单元信号.Length; i++)
            {
                if (!单元信号[i]) continue;                 // 本来就是 false，不用写
                string 地址 = 地址文本(i);
                if (!需要复位(地址)) continue;               // 状态信号（自动中/报警）不复位
                try
                {
                    // HSL V12 没有 WriteBool，写单个位用 Write(address, bool) 重载（MC 协议写位）
                    var 结果 = 网关.Write(地址, false);
                    if (!结果.IsSuccess) 失败.Add(地址 + "(" + 结果.Message + ")");
                }
                catch (Exception ex)
                {
                    失败.Add(地址 + "(" + ex.Message + ")");
                }
            }
            return 失败;
        }

        /// <summary>第 i 个单元信号对应的 PLC 地址（0=焊接OK … 8=贴标IDLE）</summary>
        public string 地址文本(int i)
        {
            int 单元 = i / 每单元信号数;
            int 信号 = i % 每单元信号数;
            if (单元 < 0 || 单元 >= 单元数) return "";
            switch (信号)
            {
                case 0: return 地址配置.单元OK[单元];
                case 1: return 地址配置.单元NG[单元];
                default: return 地址配置.单元IDLE[单元];
            }
        }

        /// <summary>
        /// 第 i 个产量触发信号对应的 PLC 地址
        /// （0=焊接OK 1=焊接NG 2=气密OK 3=气密NG 4=贴标OK 5=贴标NG，与采集循环里的 unitTrigger 下标一致）。
        /// </summary>
        public string 触发地址(int i)
        {
            int 单元 = i / 2;
            bool 是OK = (i % 2) == 0;
            if (单元 < 0 || 单元 >= 单元数) return "";
            return 是OK ? 地址配置.单元OK[单元] : 地址配置.单元NG[单元];
        }
    }
}
