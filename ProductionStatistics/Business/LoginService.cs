using System;

namespace ProductionStatistics
{
    /// <summary>
    /// 用户登录服务【V2.4.0】。
    ///
    /// 做什么：验证用户名 + 密码，通过 / 拒绝，并把每次尝试写进运行日志（谁在什么时刻登录、
    /// 失败了多少次都有据可查，**密码本身绝不写日志**）。
    ///
    /// 账号来源（当前口径）：**代码内预置**一个出厂账号 admin / 123456——软件第一次部署
    /// 无需任何配置即可登录。后续要加"多用户 / 改密码 / 权限分级"时，在这里换账号存储
    /// （比如 SQLite 用户表 或 ini 账号段），本类的 验证() 签名不变，登录窗不用改。
    ///
    /// 为什么不存哈希：预置密码本来就写在开源源码里，哈希只是把明文换了个地方放，
    /// 属于"安全剧场"；等账号真的落库（用户在界面里改密码）时再一并引入哈希存储。
    /// </summary>
    public static class LoginService
    {
        /// <summary>预置账号的用户名（出厂内置，部署即用）</summary>
        public const string 预置用户名 = "admin";

        /// <summary>预置账号的密码（出厂内置；现场上线后建议尽快在后续用户管理功能里改掉）</summary>
        public const string 预置密码 = "123456";

        /// <summary>
        /// 验证一次登录尝试。
        /// 返回 true=通过（进入主窗）；false=拒绝（登录窗显示错误提示）。
        /// 每次调用（无论成败）都写一条运行日志，便于现场追溯登录记录。
        /// </summary>
        /// <param name="用户名">登录窗输入的用户名（允许首尾空格，按 Trim 后比较）</param>
        /// <param name="密码">登录窗输入的密码（精确比较，不 Trim——密码里空格也是有效字符）</param>
        public static bool 验证(string 用户名, string 密码)
        {
            // Ordinal 精确比较：账号密码不做文化/大小写宽松处理，避免"Admin"也算通过
            bool 通过 = string.Equals((用户名 ?? "").Trim(), 预置用户名, StringComparison.Ordinal)
                     && string.Equals(密码 ?? "", 预置密码, StringComparison.Ordinal);
            if (通过)
                Log.Info("登录成功: " + 预置用户名);
            else
                // 只记输入的用户名（方便发现"输错用户名"这类问题），密码一个字符都不落日志
                Log.Info("登录失败: 用户名或密码错误 (输入用户名=" + (用户名 ?? "") + ")");
            return 通过;
        }
    }
}
