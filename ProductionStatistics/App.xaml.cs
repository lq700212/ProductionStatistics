using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ProductionStatistics
{
    /// <summary>
    /// WPF 应用入口：负责日志系统初始化、全局异常兜底、主题（深/浅）切换。
    ///
    /// 做什么：
    ///   ① 启动即初始化运行日志（logs 文件夹），任何后续异常都留得下痕迹；
    ///   ② 挂 UI 线程异常与 AppDomain 未处理异常，异常不再"无声瘫痪"；
    ///   ③ 主题切换：把 Application 资源里合并的主题字典换成另一套（Light/Dark），
    ///      控件样式里的颜色全是 DynamicResource，所以换完立即全界面生效，窗口不用重建；
    ///   ④ 用户登录【V2.4.0】：先弹登录窗（预置 admin 账号）模态验证，通过才创建主窗，
    ///      关闭登录窗 = 取消登录直接退出。
    ///
    /// 主题来源：生产统计.ini 的 [界面] 主题=深色/浅色（缺省跟随系统）。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>主题名：深色 / 浅色（对应 Themes\Dark.xaml、Themes\Light.xaml）</summary>
        public const string 深色主题 = "深色";
        public const string 浅色主题 = "浅色";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ① 日志系统先起来，后面任何一步出问题都能留痕
            Log.Init();
            Log.Info("WPF 程序启动: 版本=" + typeof(App).Assembly.GetName().Version);

            // ② 全局异常兜底：UI 线程异常弹框提示（程序不退出），未处理异常记日志
            DispatcherUnhandledException += (s, ex) =>
            {
                // 记 InnerException：WPF 的 XAML 加载异常外面裹了一层 TargetInvocationException，
                // 只记外层看不到真正原因（踩过：窗口起不来，日志里只有"调用的目标发生了异常"）
                Log.Error("UI线程异常: " + ex.Exception.Message, ex.Exception.InnerException ?? ex.Exception);
                MessageBox.Show("发生错误：" + ex.Exception.Message
                    + "\n详情见 exe 目录 logs 文件夹 与 错误日志.txt", "生产统计",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true; // 记完日志继续跑，不让现场因为一个弹框就停摆
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                Log.Error("未处理异常", ex.ExceptionObject as Exception);

            // ③ 读配置并应用主题（在窗口创建之前，窗口出来就是对的配色）
            AppConfig.载入();
            切换主题(AppConfig.取主题());

            // ④ 用户登录【V2.4.0】：先弹登录窗（模态），验证通过才创建主窗。
            //    【坑】登录窗是进程里第一个窗口，WPF 会把它认作 Application.MainWindow；
            //    若用 OnMainWindowClose/OnLastWindowClose，登录窗一关（主窗还没建）
            //    就会排队 Shutdown，把刚显示出来的主窗跟着关掉（实测：主窗初始化完成
            //    日志后紧跟"软件退出"）。所以登录期间用 OnExplicitShutdown——关登录窗
            //    不触发退出；登录通过、主窗 Show 之后再显式指定主窗并把模式切回
            //    OnMainWindowClose，此后关主窗=正常退出（走主窗 OnClosing 落库链路）。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var 登录窗 = new LoginWindow();
            if (登录窗.ShowDialog() == true)
            {
                Log.Info("登录通过，进入主窗");
                var 主窗 = new MainWindow();
                主窗.Show();
                MainWindow = 主窗;                          // 显式指定：真正的看板主窗
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            else
            {
                Log.Info("登录取消或关闭，程序退出");
                Shutdown();
            }
        }

        /// <summary>
        /// 切换主题：把 Application.Current.Resources 里合并的主题字典换成指定的那本。
        /// 颜色全部走 DynamicResource，所以换字典 = 全界面换肤，无需重建窗口。
        /// </summary>
        public static void 切换主题(string 主题名)
        {
            string 文件 = (主题名 == 深色主题) ? "Dark.xaml" : "Light.xaml";
            try
            {
                ResourceDictionary 新字典 = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Themes/" + 文件, UriKind.Absolute)
                };
                var 集合 = Application.Current.Resources.MergedDictionaries;
                // 先把旧的主题字典摘掉。
                //【V2.2.2 注意】这里只能删"本工程"的主题字典——kaleidoscope 的缺省键板/控件族
                // 字典 pack URI 里也含 "/Themes/"，误删后库语义键会静默失效（DynamicResource 缺键不报错）。
                //【V2.3.0 修复】App.xaml 里宿主主题字典写的是相对 URI "Themes/Light.xaml"（不含前导
                // 斜杠），旧判断 Contains("/Themes/") 永远匹配不到 → 旧字典删不掉、新字典追加在最后，
                // 表现为"点主题开关日志说切了、界面颜色不变"。改为按 Light/Dark 文件名精确匹配。
                for (int i = 集合.Count - 1; i >= 0; i--)
                {
                    var 源 = 集合[i].Source;
                    if (源 == null || 源.OriginalString.Contains(";component")) continue;
                    string 地址 = 源.OriginalString;
                    if (地址.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                        || 地址.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                        集合.RemoveAt(i);
                }
                集合.Add(新字典);
                Log.Info("主题已切换为: " + 主题名);
            }
            catch (Exception ex)
            {
                // 换主题失败不该让程序起不来：保留原主题继续跑
                Log.Error("切换主题失败", ex);
            }
        }
    }
}
