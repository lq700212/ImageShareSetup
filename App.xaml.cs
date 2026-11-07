using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ImageShareSetup
{
    /// <summary>
    /// 程序入口。
    ///
    /// WPF 的 DPI 由框架按设备无关单位自动换算，不再需要 WinForms 那套
    /// Graphics.DpiX 折算（这也是这次换 WPF 的原因之一：125%/150% 缩放下文字不会发虚）。
    ///
    /// 启动参数：-Tab N（0..3）直接打开到指定页签。
    /// 用途：① 现场支持时让用户"打开就停在源机连接测试页"，少点一次；
    ///       ② 自动化截图/冒烟测试可以确定性地抓每一页。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 未捕获异常兜底：WinExe 没有控制台，闪退时至少留下堆栈文件
            DispatcherUnhandledException += OnUnhandled;

            // 主题要在窗口创建前套用，否则会先闪一下深色再切浅色
            UiSettings.Load();
            ThemeManager.Apply(UiSettings.DarkTheme, false);

            int startTab = 0;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-Tab", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "/Tab", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v)) startTab = v;
                }
            }

            MainWindow w = new MainWindow(startTab);
            MainWindow = w;
            w.Show();
        }

        static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "ImageShareSetup.crash.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + e.Exception,
                    System.Text.Encoding.UTF8);
            }
            catch { }

            MessageBox.Show("程序遇到未处理的错误，详细信息已写入：\n" +
                            Path.Combine(Path.GetTempPath(), "ImageShareSetup.crash.txt"),
                            "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
