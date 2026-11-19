using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProductionStatistics
{
    /// <summary>
    /// 极简 INI 读写工具（纯文本解析，UTF-8，键值顺序不参与逻辑、按"节.键"扁平存取）。
    /// 做什么：给"生产统计.ini"（PLC IP/端口/轮询间隔/设备名称）提供最简单的读取手段。
    /// 为什么这么写：配置项很少（十来个键），不需要引入任何配置库；纯文本手工解析足够直白，
    ///   现场用记事本就能改，程序读不到就用缺省值，零依赖。
    /// 怎么改：要新增配置项，只需在 ini 里加"键=值"，代码里用 Get(ini, "节", "键", "缺省值") 取。
    /// 注意事项：
    ///   - ini 文件约定 UTF-8 编码（带 BOM），本工具写入时也用 UTF-8，别用记事本"ANSI"另存含中文的配置；
    ///   - 只支持 "key=value" 与 "[section]" 两种行，# 或 ; 开头视为注释行，够用且好排查。
    /// </summary>
    public static class SimpleIni
    {
        /// <summary>
        /// 读取整个 ini 文件，返回"节.键 → 值"的扁平字典（如 "PLC.IP" → "192.168.1.90"）。
        /// 文件不存在或解析失败都返回空字典，由调用方走缺省值，不让配置问题拖垮启动。
        /// </summary>
        public static Dictionary<string, string> Load(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return result;
                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        // 记录当前节名，后续键都归到这个节下
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    // 扁平化存键：防止不同节出现同名键互相覆盖
                    result[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch
            {
                // 配置文件坏了不该让程序起不来：返回已解析的部分/空字典，走缺省值
            }
            return result;
        }

        /// <summary>
        /// 取字符串配置；键不存在或值为空时返回缺省值。
        /// </summary>
        public static string Get(Dictionary<string, string> ini, string section, string key, string defaultValue)
        {
            string value;
            if (ini.TryGetValue(section + "." + key, out value) && value.Length > 0) return value;
            return defaultValue;
        }

        /// <summary>
        /// 取整数配置；解析失败（比如现场误填了字母）返回缺省值，不让配置把程序搞崩。
        /// </summary>
        public static int GetInt(Dictionary<string, string> ini, string section, string key, int defaultValue)
        {
            string value;
            int parsed;
            if (ini.TryGetValue(section + "." + key, out value) && int.TryParse(value, out parsed)) return parsed;
            return defaultValue;
        }
    }
}
