using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 配置读取（生产统计.ini）的小工具：整个程序只读一次 ini，读到的字典缓存起来给各处用。
    ///
    /// 为什么单独拎出来：App 启动时要读"主题"，主窗口又要读"IP/端口/设备名/语言"，
    ///   如果各读各的，就会出现"主题读的是一份 ini、其它读的是另一份"的怪事；
    ///   这里统一成一处读取（AppConfig.载入()），其它地方只取值不改值。
    ///
    /// ini 格式（与 WinForms 版完全一致，现场已有的配置文件不用改）：
    ///   [PLC]  IP / Port / 轮询间隔Ms
    ///   [界面] 设备名称 / 设备名称En / 语言 / 主题
    /// </summary>
    public static class AppConfig
    {
        /// <summary>ini 文件路径（exe 目录）</summary>
        public static string 路径 { get; private set; }

        /// <summary>解析后的 ini 字典（"节.键" → 值）</summary>
        private static Dictionary<string, string> 字典 = new Dictionary<string, string>();

        /// <summary>设备名称（中文）</summary>
        public static string 设备名称 = "点胶固化收料机";

        /// <summary>设备名称（英文）</summary>
        public static string 设备名称En = "";

        /// <summary>界面语言：false=中文 true=英文</summary>
        public static bool 是英文界面 = false;

        /// <summary>PLC 信号地址配置（来自 ini 的 [PLC地址] 段，现场可自行修改）</summary>
        public static PlcAddressConfig 地址配置 = new PlcAddressConfig();

        /// <summary>
        /// 载入 ini（不存在则生成缺省配置）。可重复调用，第二次起直接用缓存。
        /// 配置文件坏了不能让程序起不来：解析失败走缺省值（与 WinForms 版行为一致）。
        /// </summary>
        public static void 载入()
        {
            路径 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "生产统计.ini");
            if (!File.Exists(路径))
            {
                try
                {
                    File.WriteAllText(路径, 默认配置内容(), new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    Log.Warn("生成缺省配置失败: " + ex.Message);
                }
            }
            字典 = SimpleIni.Load(路径);
            地址配置 = PlcAddressConfig.从Ini(字典);
            补写地址配置段();
            设备名称 = 取("界面", "设备名称", "点胶固化收料机");
            设备名称En = 取("界面", "设备名称En", "");
            是英文界面 = 取("界面", "语言", "中文") == "English";
        }

        /// <summary>
        /// 老版本的 ini 里没有 [PLC地址] 段：自动补写一段缺省的，
        /// 这样现场升级后打开 ini 就能看到并直接改地址。
        /// </summary>
        private static void 补写地址配置段()
        {
            try
            {
                //【V2.2.2】老 ini 没有 [PLC地址] 段或仍是"信号确认OFF轮数"老键时：只补 [PLC地址] 段；
                //  "信号确认OFF轮数"老键自然失效（读取端已换成 信号确认OFF毫秒，缺省 300ms），
                //  无需回写兼容（本项目未上线，无存量包袱）。
                if (字典.ContainsKey("PLC地址.设备自动中")) return; // 已经有这段就不动它
                var 行 = new List<string>(File.ReadAllLines(路径, Encoding.UTF8));
                行.Add("");
                行.Add("# PLC 信号地址（现场可改，改完重启软件生效；缺省值见《PLC地址定义.txt》）");
                行.Add(PlcAddressConfig.缺省配置文本().TrimEnd());
                File.WriteAllLines(路径, 行, new UTF8Encoding(true));
                Log.Info("已在 生产统计.ini 里补写 [PLC地址] 段（缺省地址，可按现场实际修改）");
            }
            catch (Exception ex)
            {
                Log.Warn("补写 [PLC地址] 段失败: " + ex.Message);
            }
        }

        /// <summary>取字符串配置（键不存在或为空时返回缺省值）</summary>
        public static string 取(string 节, string 键, string 缺省值)
        {
            return SimpleIni.Get(字典, 节, 键, 缺省值);
        }

        /// <summary>取整数配置（解析失败返回缺省值）</summary>
        public static int 取整数(string 节, string 键, int 缺省值)
        {
            return SimpleIni.GetInt(字典, 节, 键, 缺省值);
        }

        /// <summary>取主题名：深色 / 浅色；ini 没配则按系统当前主题判断</summary>
        public static string 取主题()
        {
            string 配置 = 取("界面", "主题", "");
            if (配置 == App.深色主题 || 配置 == App.浅色主题) return 配置;
            // 没配就跟随系统：读注册表 AppsUseLightTheme（没有则按浅色）
            try
            {
                object 值 = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?
                    .GetValue("AppsUseLightTheme");
                if (值 != null) return Convert.ToInt32(值) == 1 ? App.浅色主题 : App.深色主题;
            }
            catch
            {
                // 读不到注册表就用缺省
            }
            return App.浅色主题;
        }

        /// <summary>
        /// 把主题写回 ini（下次启动仍生效）；写失败只记日志，不打断使用。
        /// 注意：先算出 [界面] 节的行范围再改，不要边遍历边往列表里插（会索引错乱、还会重复追加注释）。
        /// </summary>
        public static void 保存主题(string 主题名)
        {
            try
            {
                var 行 = new List<string>();
                if (File.Exists(路径)) 行.AddRange(File.ReadAllLines(路径, Encoding.UTF8));

                // 找 [界面] 节的起止行号
                int 节起 = -1, 节止 = 行.Count;
                for (int i = 0; i < 行.Count; i++)
                {
                    string 文本 = 行[i].Trim();
                    if (文本.StartsWith("[") && 文本.EndsWith("]"))
                    {
                        if (节起 >= 0) { 节止 = i; break; }   // 遇到下一个节，[界面] 段结束
                        if (文本 == "[界面]") 节起 = i;
                    }
                }
                if (节起 < 0)
                {
                    // ini 里根本没有 [界面] 段：补一个
                    行.Add("");
                    行.Add("[界面]");
                    节起 = 行.Count - 1;
                    节止 = 行.Count;
                }

                // 在 [界面] 段里找 "主题=" 行
                bool 找到 = false;
                for (int i = 节起 + 1; i < 节止; i++)
                {
                    if (行[i].Trim().StartsWith("主题="))
                    {
                        行[i] = "主题=" + 主题名;
                        找到 = true;
                        break;
                    }
                }
                if (!找到)
                {
                    // 没有这一项：在段末插入（注释只插一次，不会每次切主题都多一行）
                    行.Insert(节止, "# 主题：深色 / 浅色（运行中点右上角开关可切换，这里保存的是下次启动的缺省）");
                    行.Insert(节止 + 1, "主题=" + 主题名);
                }
                File.WriteAllLines(路径, 行, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Warn("保存主题设置失败: " + ex.Message);
            }
        }

        /// <summary>首次运行生成的缺省配置（与现场《生产统计.ini》口径一致，老 ini 继续可用）</summary>
        private static string 默认配置内容()
        {
            return "# 生产统计 配置文件（UTF-8）\r\n" +
                   "[PLC]\r\nIP=192.168.1.156\r\nPort=6060\r\n" +
                   "# 采集周期（毫秒）。越小越不容易漏掉 PLC 的短触发脉冲，但 PLC 通讯负担越大。\r\n" +
                   "轮询间隔Ms=100\r\n" +
                   "# 计数去抖：连续读到 OFF 持续多少毫秒，才认为\"这个触发信号真的结束了\"（默认 300）。\r\n" +
                   "# 用于防止同一件产品被重复计数（自锁位被复位后 PLC 很快又重新置位时会读到短暂 OFF）。\r\n" +
                   "# 现场实测复位空档约 230ms，默认 300ms = 空档 + 30% 余量。\r\n" +
                   "信号确认OFF毫秒=300\r\n" +
                   "# 计数去抖：同一个触发位两次计数之间的最小间隔（毫秒，默认 1500）。\r\n" +
                   "# 必须大于\"复位后 PLC 重新置位延迟 + 一个采样周期\"，又要小于现场真实生产节拍。\r\n" +
                   "计数最小间隔Ms=1500\r\n" +
                   "\r\n" +
                   "[界面]\r\n设备名称=点胶固化收料机\r\n" +
                   "# 设备名称En：英文界面显示的设备名（缺省给了对照翻译，可按现场实际英文名改；别用&，Label 会吃掉）\r\n" +
                   "设备名称En=Dispensing-Curing-Collecting Machine\r\n" +
                   "# 语言：中文 / English（决定启动时的默认界面语言，运行中可用界面按钮切换）\r\n" +
                   "语言=中文\r\n" +
                   "# 主题：深色 / 浅色（仅 WPF 版；缺省跟随系统）\r\n" +
                   "主题=浅色\r\n" +
                   "\r\n" +
                   "# PLC 信号地址（现场可改，改完重启软件生效）\r\n" +
                   PlcAddressConfig.缺省配置文本();
        }
    }
}
