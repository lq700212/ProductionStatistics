using System;
using System.Windows;
using System.Windows.Input;
using Kaleidoscope.UI.Views;

namespace ProductionStatistics
{
    /// <summary>
    /// 登录窗【V2.4.0】——软件启动的第一道门。
    ///
    /// 做什么：
    ///   App.OnStartup 在主窗创建**之前**弹出本窗（ShowDialog 模态），输入用户名 + 密码
    ///   验证通过（预置 admin / 123456，见 Business\LoginService.cs）才创建主窗与采集线程；
    ///   关闭本窗（× / 任务栏关闭）= 放弃登录，程序直接退出。
    ///
    /// 运行流程：
    ///   构造 → 按 ini 里的语言设置界面文字 → 焦点落用户名框
    ///   登录 → LoginService.验证() → 通过：DialogResult=true（App 收到后 new MainWindow）
    ///                              → 失败：显示红字错误 + 清空密码框重新输入
    ///
    /// 线程纪律：本窗在 UI 线程显示/验证（账号验证是纯内存比较，无 IO，不卡界面）。
    ///
    /// 骨架说明：继承库 ChromeDialogWindow（圆角/标题条/× 按钮全由基类负责），
    /// 控件全用 Kaleidoscope.UI 控件族（KxTextBox / KxPasswordBox / KxPrimaryButton /
    /// StatusChipText 语义色），与主窗同一套风格，深浅主题自动跟随。
    /// </summary>
    public partial class LoginWindow : ChromeDialogWindow
    {
        /// <summary>界面语言：false=中文 true=英文（取 ini 已保存的语言设置，登录后主窗沿用）</summary>
        private bool 英文界面;

        /// <summary>构造：初始化界面 → 按保存的语言设置文字 → 焦点落用户名框</summary>
        public LoginWindow()
        {
            InitializeComponent();
            英文界面 = AppConfig.是英文界面;
            应用语言();
            Loaded += (s, e) => 用户名输入.Focus();
        }

        /// <summary>登录按钮 / 用户名·密码框里按回车：统一走 尝试登录()</summary>
        private void 登录按钮_Click(object sender, RoutedEventArgs e)
        {
            尝试登录();
        }

        /// <summary>输入框回车=登录（操作员习惯：输完密码直接敲回车，不必再点按钮）</summary>
        private void 输入框_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) 尝试登录();
        }

        /// <summary>
        /// 尝试登录：空输入直接提示；否则交给 LoginService.验证()。
        /// 通过 → DialogResult=true（关窗，App 继续创建主窗）；
        /// 失败 → 红字提示 + 清空密码框 + 焦点回密码框（用户名通常没错，只重输密码）。
        /// </summary>
        private void 尝试登录()
        {
            string 用户名 = 用户名输入.Text;
            string 密码 = 密码输入.Password;

            if (string.IsNullOrWhiteSpace(用户名) || string.IsNullOrEmpty(密码))
            {
                显示错误(英文界面 ? "Enter username and password." : "请输入用户名和密码。");
                return;
            }

            if (LoginService.验证(用户名, 密码))
            {
                DialogResult = true;   // 关窗；App.OnStartup 收到 true 后创建主窗
            }
            else
            {
                显示错误(英文界面 ? "Wrong username or password." : "用户名或密码错误。");
                密码输入.Clear();
                密码输入.Focus();
            }
        }

        /// <summary>显示错误提示（StatusChipText Tag=Ng 红字，默认 Collapsed）</summary>
        private void 显示错误(string 文字)
        {
            错误提示.Text = 文字;
            错误提示.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 按当前语言设置本窗全部静态文字（登录先于主窗，主窗的 应用语言() 管不到这里；
        /// 语言来源 = ini 保存值，与主窗启动语言一致，不会出现"登录中文、主窗英文"）。
        /// </summary>
        private void 应用语言()
        {
            Title = 英文界面 ? "Sign in" : "用户登录";
            登录标题.Text = 英文界面 ? "Production Statistics" : "生产统计";
            登录副标题.Text = "Machine Name: " + 设备显示名();
            用户名标签.Text = 英文界面 ? "Username" : "用户名";
            密码标签.Text = 英文界面 ? "Password" : "密码";
            登录按钮.Content = 英文界面 ? "Sign in" : "登 录";
            // 版本号取程序集版本（与 exe 同源，CHANGELOG 定版时同步）
            版本文字.Text = (英文界面 ? "Production Statistics V" : "生产统计 V")
                + typeof(App).Assembly.GetName().Version;
        }

        /// <summary>设备显示名（与主窗同口径：中文界面用中文名，英文界面用 ini 英文名）</summary>
        private string 设备显示名()
        {
            if (!英文界面) return AppConfig.设备名称;
            if (AppConfig.设备名称En.Length > 0) return AppConfig.设备名称En;
            if (AppConfig.设备名称 == "点胶固化收料机") return "Dispensing-Curing-Collecting Machine";
            return AppConfig.设备名称;
        }
    }
}
