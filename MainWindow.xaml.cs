using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageShareSetup.Core;

namespace ImageShareSetup
{
    /// <summary>
    /// 主窗口业务逻辑。
    ///
    /// 分工：
    ///   · 页①（目标机配置）：调 Setup-ImageShare.ps1，流式日志 + 成功后列出本机 UNC。
    ///   · 页②（源机连接测试）：纯 C# 六步检测（不经过 PowerShell），实时更新每一步。
    ///   · 页③（旧图清理）：C# 先扫描预览（只读、安全），确认后调 Clear-OldImages.ps1 -Execute。
    ///   · 页④（说明）：纯静态内容 + 打开目录按钮。
    ///
    /// 线程模型：
    ///   · 所有脚本/磁盘 IO 都在后台线程；UI 更新统一 Dispatcher.BeginInvoke。
    ///   · 事件处理里的异常必须自己接住，否则 async void 会直接把进程带崩。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---- 运行状态 ------------------------------------------------------
        readonly int mStartTab;
        bool mIsAdmin;
        bool mBusy;
        int mCurrentTab;

        ScriptRunner mSetupRunner;
        CancellationTokenSource mSetupCts;

        ScriptRunner mCleanRunner;
        CancellationTokenSource mTestCts;

        readonly ObservableCollection<TestStep> mSteps = new ObservableCollection<TestStep>();
        List<DiskInfo> mDisks = new List<DiskInfo>();

        // 清理时的"扫描快照"：执行删除只认扫描时的根目录和天数，
        // 防止用户扫描后又改了输入框，结果删到别的范围（安全红线）。
        string mScanRoot;
        int mScanDays;

        // 页①最近一次成功的共享路径（供"复制共享路径"用）
        string mLastSharePath;

        bool mPasswordVisible;
        bool mPasswordVisible2;

        public MainWindow() : this(0)
        {
        }

        public MainWindow(int startTab)
        {
            mStartTab = Math.Max(0, Math.Min(3, startTab));
            InitializeComponent();

            Loaded += OnLoaded;
            StateChanged += OnStateChanged;
            Closing += OnClosing;
        }

        // =====================================================================
        // 初始化
        // =====================================================================

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            mIsAdmin = IsAdministrator();
            RefreshAdminUI();

            txtTheme.Text = ThemeManager.IsDark ? "浅色模式" : "深色模式";
            txtVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            // 上次填写的信息（密码不保存）
            txtServer.Text = UiSettings.Server ?? "";
            txtShare.Text = string.IsNullOrWhiteSpace(UiSettings.Share) ? "VisionImages" : UiSettings.Share;
            txtUser2.Text = string.IsNullOrWhiteSpace(UiSettings.User) ? "visionshare" : UiSettings.User;
            txtShareName.Text = txtShare.Text;
            txtUser.Text = txtUser2.Text;
            txtCleanRoot.Text = UiSettings.CleanRoot ?? "";
            txtDays.Text = (UiSettings.KeepDays > 0 ? UiSettings.KeepDays : 30).ToString();

            InitSteps();
            LoadDisks();
            ShowPage(mStartTab);
            UpdateMaxIcon();
        }

        void InitSteps()
        {
            mSteps.Clear();
            mSteps.Add(new TestStep("① 网络可达（TCP 445）"));
            mSteps.Add(new TestStep("② 保存 Windows 凭据"));
            mSteps.Add(new TestStep("③ 访问共享目录"));
            mSteps.Add(new TestStep("④ 写入测试（8 MB）"));
            mSteps.Add(new TestStep("⑤ 读回校验"));
            mSteps.Add(new TestStep("⑥ 读取剩余空间"));
            lstSteps.ItemsSource = mSteps;
        }

        void RefreshAdminUI()
        {
            if (bannerAdmin == null) return;

            bannerAdmin.Visibility = mIsAdmin ? Visibility.Collapsed : Visibility.Visible;
            btnElevate2.Visibility = mIsAdmin ? Visibility.Collapsed : Visibility.Visible;
            txtAdminState.Text = mIsAdmin ? "管理员权限" : "普通权限（可测试/清理，不能建共享）";
            dotAdmin.Fill = (Brush)FindResource(mIsAdmin ? "OkBrush" : "WarnBrush");
        }

        static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            try
            {
                // 关窗口时把还在跑的脚本停掉，否则后台 powershell 会一直挂着
                if (mSetupRunner != null) mSetupRunner.Stop();
                if (mSetupCts != null) mSetupCts.Cancel();
                if (mTestCts != null) mTestCts.Cancel();

                UiSettings.Server = txtServer.Text.Trim();
                UiSettings.Share = string.IsNullOrWhiteSpace(txtShare.Text) ? "VisionImages" : txtShare.Text.Trim();
                UiSettings.User = string.IsNullOrWhiteSpace(txtUser2.Text) ? "visionshare" : txtUser2.Text.Trim();
                UiSettings.CleanRoot = txtCleanRoot.Text.Trim();
                int days;
                if (int.TryParse(txtDays.Text.Trim(), out days) && days > 0) UiSettings.KeepDays = days;
                UiSettings.Save();
            }
            catch { }
        }

        // =====================================================================
        // 标题栏 / 导航 / 主题
        // =====================================================================

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // XAML 加载早期 nav0.IsChecked=True 会先触发一次，此时 page0 还没赋值
            if (page0 == null) return;

            RadioButton rb = sender as RadioButton;
            int idx;
            if (rb != null && int.TryParse(Convert.ToString(rb.Tag), out idx))
                ShowPage(idx);
        }

        void ShowPage(int idx)
        {
            if (idx < 0 || idx > 3) idx = 0;
            if (page0 == null) return;

            mCurrentTab = idx;

            page0.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            page1.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            page2.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            page3.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;

            RadioButton[] navs = { nav0, nav1, nav2, nav3 };
            if (navs[idx].IsChecked != true) navs[idx].IsChecked = true;

            switch (idx)
            {
                case 0: txtNavHint.Text = "目标机：配置共享"; break;
                case 1: txtNavHint.Text = "源机：测试连接"; break;
                case 2: txtNavHint.Text = "清理过期日期目录"; break;
                default: txtNavHint.Text = "部署流程 / 换盘 SOP"; break;
            }
        }

        void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Apply(!ThemeManager.IsDark);
            txtTheme.Text = ThemeManager.IsDark ? "浅色模式" : "深色模式";

            // 直接设置过 Brush 的地方要手动刷新一遍（主题替换后旧 Brush 对象不会自动变）
            RefreshAdminUI();
            dotBusy.Fill = (Brush)FindResource(mBusy ? "WarnBrush" : "OkBrush");
        }

        void Min_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        void Max_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        void OnStateChanged(object sender, EventArgs e)
        {
            UpdateMaxIcon();
        }

        void UpdateMaxIcon()
        {
            if (icoMax == null) return;
            icoMax.Data = WindowState == WindowState.Maximized
                ? Geometry.Parse("M7,7 H17 V17 H7 Z M4,10 V19 H14")
                : Geometry.Parse("M5,5 H19 V19 H5 Z");
        }

        void SetBusy(bool busy, string text)
        {
            mBusy = busy;
            txtStatus.Text = text;
            dotBusy.Fill = (Brush)FindResource(busy ? "WarnBrush" : "OkBrush");
        }

        // =====================================================================
        // 页① 目标机配置
        // =====================================================================

        void RefreshDisks_Click(object sender, RoutedEventArgs e)
        {
            LoadDisks();
        }

        void LoadDisks()
        {
            mDisks = DiskScanner.Scan();
            lbDisks.ItemsSource = mDisks;
            txtNoDisk.Visibility = mDisks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (mDisks.Count > 0)
            {
                // 优先选卷标匹配的数据盘（换盘后的新盘），其次选第一块（容量最大）
                string wantLabel = txtVolumeLabel.Text.Trim();
                int index = mDisks.FindIndex(d =>
                    !string.IsNullOrWhiteSpace(wantLabel) &&
                    string.Equals(d.Label, wantLabel, StringComparison.OrdinalIgnoreCase));
                if (index < 0) index = 0;

                lbDisks.SelectedIndex = index;
            }
            else
            {
                UpdateRootPreview();
            }
        }

        void Disks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRootPreview();
        }

        void UpdateRootPreview()
        {
            if (txtRootPreview == null) return;

            DiskInfo disk = lbDisks.SelectedItem as DiskInfo;
            if (disk == null)
            {
                txtRootPreview.Text = "（先选择一块磁盘）";
                return;
            }

            string folder = "VisionImages";
            txtRootPreview.Text = disk.Letter + ":\\" + folder;
        }

        void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            mPasswordVisible = !mPasswordVisible;
            if (mPasswordVisible)
            {
                txtPwdPlain.Text = pwdBox.Password;
                pwdBox.Visibility = Visibility.Collapsed;
                txtPwdPlain.Visibility = Visibility.Visible;
            }
            else
            {
                pwdBox.Password = txtPwdPlain.Text;
                txtPwdPlain.Visibility = Visibility.Collapsed;
                pwdBox.Visibility = Visibility.Visible;
            }
        }

        void RandomPassword_Click(object sender, RoutedEventArgs e)
        {
            string pwd = NewRandomPassword();
            if (mPasswordVisible)
            {
                txtPwdPlain.Text = pwd;
                pwdBox.Password = pwd;
            }
            else
            {
                pwdBox.Password = pwd;
                txtPwdPlain.Text = pwd;
            }
            txtSetupLog.AppendText("已生成随机密码：" + pwd + Environment.NewLine +
                                   "（点“开始配置”后，它也会写进目标机的 连接信息.txt）" + Environment.NewLine);
            txtSetupLog.ScrollToEnd();
        }

        string GetPassword1()
        {
            string pwd = mPasswordVisible ? txtPwdPlain.Text : pwdBox.Password;
            return (pwd ?? string.Empty).Trim();
        }

        static string NewRandomPassword()
        {
            // 去掉 0/O/1/l/I 等易混字符；固定加 Vs@ 满足 Windows 复杂度要求
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            byte[] bytes = new byte[10];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder("Vs@");
            foreach (byte b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        async void Setup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!mIsAdmin)
                {
                    MessageBoxResult r = MessageBox.Show(
                        "建共享、改盘符、设权限需要管理员权限。\n\n现在以管理员身份重启本工具吗？",
                        "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r == MessageBoxResult.Yes) RestartAsAdmin();
                    return;
                }

                DiskInfo disk = lbDisks.SelectedItem as DiskInfo;
                if (disk == null)
                {
                    MessageBox.Show("请先选择一块存图磁盘。\n\n如果列表是空的，请到「磁盘管理」把硬盘初始化并格式化 NTFS 后再刷新。",
                        "没有可用磁盘", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string share = txtShareName.Text.Trim();
                string user = txtUser.Text.Trim();
                string label = txtVolumeLabel.Text.Trim();
                if (share.Length == 0 || user.Length == 0)
                {
                    MessageBox.Show("共享名和专用账号不能为空。", "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await RunSetupAsync(disk, share, user, label, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("执行配置时出错：\n" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        async void Repair_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!mIsAdmin)
                {
                    MessageBoxResult r = MessageBox.Show(
                        "修复共享需要管理员权限。\n\n现在以管理员身份重启本工具吗？",
                        "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r == MessageBoxResult.Yes) RestartAsAdmin();
                    return;
                }

                DiskInfo disk = lbDisks.SelectedItem as DiskInfo;
                if (disk == null)
                {
                    MessageBox.Show("请先选择存图磁盘（换盘后就选新盘）。", "没有选中磁盘",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string share = txtShareName.Text.Trim();
                string user = txtUser.Text.Trim();
                string label = txtVolumeLabel.Text.Trim();
                if (share.Length == 0 || user.Length == 0)
                {
                    MessageBox.Show("共享名和专用账号不能为空。", "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await RunSetupAsync(disk, share, user, label, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("执行修复时出错：\n" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        async Task RunSetupAsync(DiskInfo disk, string share, string user, string label, bool repair)
        {
            mSetupCts = new CancellationTokenSource();
            SetSetupRunning(true);
            txtSetupLog.Clear();
            cardShareResult.Visibility = Visibility.Collapsed;
            SetBusy(true, repair ? "正在修复共享…" : "正在配置共享…");

            List<string> args = new List<string>();
            if (repair) args.Add("-Repair");
            args.Add("-DataDrive"); args.Add(disk.Letter);
            args.Add("-ShareName"); args.Add(share);
            args.Add("-User"); args.Add(user);
            if (!string.IsNullOrWhiteSpace(label))
            {
                args.Add("-VolumeLabel"); args.Add(label);
            }

            // 密码留空 = 首次自动生成 / 已配置则沿用原密码（脚本内部逻辑），不传参数即可
            string pwd = GetPassword1();
            if (!string.IsNullOrWhiteSpace(pwd))
            {
                args.Add("-Password"); args.Add(pwd);
            }

            // “连接信息.txt”写到 exe 旁边：内嵌脚本跑在 %TEMP%，写那里现场找不到密码
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) exeDir = AppDomain.CurrentDomain.BaseDirectory;
            args.Add("-InfoFile");
            args.Add(Path.Combine(exeDir, "连接信息.txt"));

            if (chkDisableHibernate.IsChecked == true) args.Add("-DisableHibernate");
            if (chkRecursiveAcl.IsChecked == true) args.Add("-RecursiveAcl");

            AppendLog(txtSetupLog, repair ? "开始执行「换盘修复」…" : "开始执行「首次配置」…");

            try
            {
                mSetupRunner = new ScriptRunner();
                mSetupRunner.Output += OnSetupOutput;

                string script = EmbeddedScripts.ResolveScript("Setup-ImageShare.ps1");
                int code = await mSetupRunner.RunAsync(script, args.ToArray(), mSetupCts.Token);

                if (code == 0)
                {
                    AppendLog(txtSetupLog, "");
                    AppendLog(txtSetupLog, "================ 配置成功 ================");
                    ShowShareResult(share);
                    LoadDisks(); // 盘符可能被脚本改过，刷新一下
                }
                else
                {
                    AppendLog(txtSetupLog, "");
                    AppendLog(txtSetupLog, "================ 配置失败（退出码 " + code + "）================");
                    AppendLog(txtSetupLog, "请把上面的红色/黄色提示拍下来，或运行 Run-Setup.cmd 看完整输出。");

                    // 兜底诊断：脚本不认识界面传的参数（一般是 exe 同级 Scripts\ 里放了旧脚本）。
                    // 参数绑定失败时脚本体不会执行，这里明确告诉现场怎么恢复。
                    string logText = txtSetupLog.Text ?? "";
                    if (logText.IndexOf("PositionalParameterNotFound", StringComparison.Ordinal) >= 0 ||
                        logText.IndexOf("找不到接受实际参数", StringComparison.Ordinal) >= 0)
                    {
                        AppendLog(txtSetupLog, "");
                        AppendLog(txtSetupLog, "【参数不匹配】脚本不认识界面传来的参数，配置动作一行都没执行。");
                        AppendLog(txtSetupLog, "处理：先用最新 ImageShareSetup.exe 覆盖；若 exe 同级有 Scripts 目录，");
                        AppendLog(txtSetupLog, "      里面的 ps1 是旧版，删掉它（程序会用内置脚本）后重试。");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog(txtSetupLog, "[界面] 执行出错：" + ex.Message);
            }
            finally
            {
                SetSetupRunning(false);
                SetBusy(false, "就绪");
                mSetupRunner = null;
                mSetupCts = null;
            }
        }

        void OnSetupOutput(string line)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => AppendLog(txtSetupLog, line)));
        }

        void SetSetupRunning(bool running)
        {
            btnSetup.IsEnabled = !running;
            btnRepair.IsEnabled = !running;
            btnRefreshDisks.IsEnabled = !running;
            lbDisks.IsEnabled = !running;
            txtShareName.IsEnabled = !running;
            txtUser.IsEnabled = !running;
            txtVolumeLabel.IsEnabled = !running;
            pwdBox.IsEnabled = !running;
            txtPwdPlain.IsEnabled = !running;
            chkDisableHibernate.IsEnabled = !running;
            chkRecursiveAcl.IsEnabled = !running;
            btnCancelSetup.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            if (!running) UpdateRootPreview();
        }

        void CancelSetup_Click(object sender, RoutedEventArgs e)
        {
            if (mSetupCts != null) mSetupCts.Cancel();
            if (mSetupRunner != null) mSetupRunner.Stop();
            AppendLog(txtSetupLog, "[界面] 已请求停止脚本…");
        }

        void AppendLog(TextBox box, string line)
        {
            if (box == null) return;
            box.AppendText((line ?? string.Empty) + Environment.NewLine);
            box.ScrollToEnd();
        }

        /// <summary>配置成功后列出本机每个 IPv4 对应的 UNC 路径，源机照抄即可。</summary>
        void ShowShareResult(string share)
        {
            spSharePaths.Children.Clear();
            mLastSharePath = "";

            List<string> ips = GetIPv4List();
            if (ips.Count == 0)
            {
                TextBlock tip = new TextBlock
                {
                    Text = "没有取到本机 IPv4 地址。请在目标机运行 ipconfig 查看，路径格式：\\\\<IP>\\" + share,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("SubTextBrush"),
                    FontSize = 12.5
                };
                spSharePaths.Children.Add(tip);
            }
            else
            {
                foreach (string ip in ips)
                {
                    string unc = "\\\\" + ip + "\\" + share;
                    if (mLastSharePath.Length == 0) mLastSharePath = unc;
                    spSharePaths.Children.Add(BuildPathRow(unc, "本机网卡 " + ip));
                }
            }

            cardShareResult.Visibility = Visibility.Visible;
        }

        Border BuildPathRow(string unc, string caption)
        {
            Border border = new Border { Style = (Style)FindResource("CardFlat"), Margin = new Thickness(0, 0, 0, 6) };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel stack = new StackPanel();
            TextBlock cap = new TextBlock
            {
                Text = caption,
                FontSize = 11,
                Foreground = (Brush)FindResource("SubTextBrush")
            };
            TextBlock path = new TextBlock
            {
                Text = unc,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 14,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(cap);
            stack.Children.Add(path);

            Button copy = new Button
            {
                Content = "复制",
                Style = (Style)FindResource("SmallGhostButton"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            string captured = unc;
            copy.Click += (s, e) => SafeCopy(captured, "共享路径已复制");

            Grid.SetColumn(stack, 0);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(stack);
            grid.Children.Add(copy);

            border.Child = grid;
            return border;
        }

        void CopySharePath_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(mLastSharePath))
            {
                MessageBox.Show("还没有可复制的共享路径，请先完成配置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SafeCopy(mLastSharePath, "共享路径已复制：\n" + mLastSharePath);
        }

        void GoConnect_Click(object sender, RoutedEventArgs e)
        {
            List<string> ips = GetIPv4List();
            if (ips.Count > 0) txtServer.Text = ips[0];
            txtShare.Text = txtShareName.Text.Trim();
            ShowPage(1);
        }

        /// <summary>取本机所有可用 IPv4（排除回环/169.254 自动地址），内网 192.168 优先。</summary>
        static List<string> GetIPv4List()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }
            }
            catch { }

            return list
                .OrderBy(ip => ip.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(ip => ip, StringComparer.Ordinal)
                .ToList();
        }

        void RestartAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "-Tab " + mCurrentTab
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Win32Exception)
            {
                // 用户在 UAC 上点了"否"，什么都不做
            }
            catch (Exception ex)
            {
                MessageBox.Show("重启失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void Elevate_Click(object sender, RoutedEventArgs e)
        {
            RestartAsAdmin();
        }

        // =====================================================================
        // 页② 源机连接测试
        // =====================================================================

        void TogglePassword2_Click(object sender, RoutedEventArgs e)
        {
            mPasswordVisible2 = !mPasswordVisible2;
            if (mPasswordVisible2)
            {
                txtPwdPlain2.Text = pwdBox2.Password;
                pwdBox2.Visibility = Visibility.Collapsed;
                txtPwdPlain2.Visibility = Visibility.Visible;
            }
            else
            {
                pwdBox2.Password = txtPwdPlain2.Text;
                txtPwdPlain2.Visibility = Visibility.Collapsed;
                pwdBox2.Visibility = Visibility.Visible;
            }
        }

        string GetPassword2()
        {
            string pwd = mPasswordVisible2 ? txtPwdPlain2.Text : pwdBox2.Password;
            return (pwd ?? string.Empty).Trim();
        }

        async void Test_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string server, shareFromInput;
                ConnectTester.ParseServer(txtServer.Text, out server, out shareFromInput);

                string share = string.IsNullOrWhiteSpace(shareFromInput) ? txtShare.Text.Trim() : shareFromInput;
                string user = txtUser2.Text.Trim();
                string pwd = GetPassword2();

                if (string.IsNullOrWhiteSpace(server))
                {
                    MessageBox.Show("请填写目标机 IP，例如 192.168.10.50。", "请检查输入",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(share))
                {
                    MessageBox.Show("请填写共享名，默认 VisionImages。", "请检查输入",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(user))
                {
                    MessageBox.Show("请填写共享账号。", "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(pwd))
                {
                    MessageBox.Show("请填写密码。\n\n密码见目标机的「连接信息.txt」；没有的话到目标机第①页重新配置/重置一次。",
                        "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ResetSteps();
                cardTestResult.Visibility = Visibility.Collapsed;
                SetTestRunning(true);
                txtTestStatus.Text = "正在检测…";
                SetBusy(true, "正在测试目标机连接…");

                mTestCts = new CancellationTokenSource();
                ConnectTestResult result = await ConnectTester.RunAsync(
                    server, share, user, pwd, mSteps.ToArray(), ConnectTester.DefaultSizeMB, mTestCts.Token);

                ShowTestResult(result);

                if (result.Success)
                {
                    // 页③默认用同一个根路径，省得再抄一遍
                    if (string.IsNullOrWhiteSpace(txtCleanRoot.Text)) txtCleanRoot.Text = result.UncPath;

                    UiSettings.Server = server;
                    UiSettings.Share = share;
                    UiSettings.User = user;
                    UiSettings.Save();
                }
            }
            catch (OperationCanceledException)
            {
                txtTestStatus.Text = "已停止";
            }
            catch (Exception ex)
            {
                txtTestStatus.Text = "检测出错";
                MessageBox.Show("检测过程中出错：\n" + ex.Message, "出错了",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetTestRunning(false);
                SetBusy(false, "就绪");
                mTestCts = null;
            }
        }

        void ResetSteps()
        {
            foreach (TestStep s in mSteps)
            {
                s.State = StepState.Pending;
                s.Detail = "等待检测";
            }
        }

        void ShowTestResult(ConnectTestResult r)
        {
            cardTestResult.Visibility = Visibility.Visible;
            txtUncPath.Text = r.UncPath ?? "";

            if (r.Success)
            {
                testResultIcon.Background = (Brush)FindResource("OkGradient");
                txtTestIcon.Text = "✓";
                txtTestTitle.Text = string.Format("连接正常（写入约 {0:0.0} MB/s）", r.SpeedMBps);
                txtTestMessage.Text = r.Message +
                    (r.FreeGB > 0 ? string.Format(" 目标盘剩余 {0} GB / 总 {1} GB。", r.FreeGB, r.TotalGB) : "");
                txtTestStatus.Text = "六步全绿";
            }
            else
            {
                testResultIcon.Background = (Brush)FindResource("DangerGradient");
                txtTestIcon.Text = "×";
                txtTestTitle.Text = "连接失败";
                txtTestMessage.Text = r.Message ?? "未知原因。";
                txtTestStatus.Text = "有步骤失败，看下方原因";
            }
        }

        void StopTest_Click(object sender, RoutedEventArgs e)
        {
            if (mTestCts != null)
            {
                mTestCts.Cancel();
                txtTestStatus.Text = "正在停止…";
            }
        }

        void SetTestRunning(bool running)
        {
            btnTest.IsEnabled = !running;
            txtServer.IsEnabled = !running;
            txtShare.IsEnabled = !running;
            txtUser2.IsEnabled = !running;
            pwdBox2.IsEnabled = !running;
            txtPwdPlain2.IsEnabled = !running;
            btnStopTest.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        void ClearCred_Click(object sender, RoutedEventArgs e)
        {
            string server, share;
            ConnectTester.ParseServer(txtServer.Text, out server, out share);
            if (string.IsNullOrWhiteSpace(server))
            {
                MessageBox.Show("请先填写目标机 IP。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int rc = ConnectTester.CmdKeyDelete(server);
            if (rc == 0)
                MessageBox.Show("已删除本机保存的凭据：" + server + "\n\n下次连接会重新使用这里填写的账号密码。",
                    "已清理", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("没有找到该目标机的凭据，或删除失败（退出码 " + rc + "）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OpenUnc_Click(object sender, RoutedEventArgs e)
        {
            string unc = GetCurrentUnc();
            if (string.IsNullOrWhiteSpace(unc)) return;
            try
            {
                Process.Start("explorer.exe", "\"" + unc + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开共享失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        string GetCurrentUnc()
        {
            string server, share;
            ConnectTester.ParseServer(txtServer.Text, out server, out share);
            if (string.IsNullOrWhiteSpace(share)) share = txtShare.Text.Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(share))
            {
                MessageBox.Show("请先填写目标机 IP 和共享名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return ConnectTester.BuildUnc(server, share);
        }

        void CopyUnc_Click(object sender, RoutedEventArgs e)
        {
            string unc = txtUncPath.Text;
            if (string.IsNullOrWhiteSpace(unc)) unc = GetCurrentUnc();
            if (string.IsNullOrWhiteSpace(unc)) return;
            SafeCopy(unc, "路径已复制：\n" + unc);
        }

        // =====================================================================
        // 页③ 旧图清理
        // =====================================================================

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            await RunScanAsync();
        }

        async Task RunScanAsync()
        {
            string root = txtCleanRoot.Text.Trim();
            int days;
            if (string.IsNullOrWhiteSpace(root))
            {
                MessageBox.Show("请填写存图根目录，例如 \\\\192.168.10.50\\VisionImages 或 D:\\VisionImages。",
                    "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(txtDays.Text.Trim(), out days) || days < 1)
            {
                MessageBox.Show("保留天数必须是大于 0 的整数。", "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(root))
            {
                MessageBox.Show("目录不存在或无法访问：\n" + root +
                    "\n\nUNC 路径请先在资源管理器里确认能打开（凭据没配好时这里也打不开）。",
                    "无法访问", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnScan.IsEnabled = false;
            btnClean.IsEnabled = false;
            txtCleanStatus.Text = "正在扫描（文件多时需要一会儿）…";
            SetBusy(true, "正在扫描日期目录…");

            try
            {
                List<CleanItem> items = await Task.Run(() => ScanDateDirs(root, days));

                lbCleanItems.ItemsSource = items;
                int delCount = items.Count(i => i.WillDelete);
                long delBytes = items.Where(i => i.WillDelete).Sum(i => i.SizeBytes);

                txtCleanSummary.Text = string.Format("共 {0} 个日期目录 · 将删除 {1} 个（约 {2}）",
                    items.Count, delCount, FormatSize(delBytes));
                txtCleanEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                btnClean.IsEnabled = delCount > 0;
                txtCleanStatus.Text = delCount > 0 ? "扫描完成，确认后点「执行清理」" : "没有超过保留天数的目录";

                mScanRoot = root;
                mScanDays = days;
            }
            catch (Exception ex)
            {
                txtCleanStatus.Text = "扫描失败";
                MessageBox.Show("扫描失败：\n" + ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnScan.IsEnabled = true;
                SetBusy(false, "就绪");
            }
        }

        /// <summary>
        /// 扫描日期目录（只读，不删任何东西）。
        /// 结构兼容：root\20261106\... 和 root\料号\20261106\...（与脚本规则一致）。
        /// </summary>
        static List<CleanItem> ScanDateDirs(string root, int days)
        {
            DateTime cutoff = DateTime.Today.AddDays(-days);
            List<KeyValuePair<string, string>> dateDirs = new List<KeyValuePair<string, string>>();

            foreach (string dir in SafeEnumerateDirs(root))
            {
                string name = Path.GetFileName(dir);
                if (IsDateName(name))
                {
                    dateDirs.Add(new KeyValuePair<string, string>(dir, name));
                }
                else
                {
                    // 旧模板：第一层是料号，第二层才是日期
                    foreach (string sub in SafeEnumerateDirs(dir))
                    {
                        string subName = Path.GetFileName(sub);
                        if (IsDateName(subName))
                            dateDirs.Add(new KeyValuePair<string, string>(sub, subName));
                    }
                }
            }

            List<CleanItem> result = new List<CleanItem>();
            foreach (KeyValuePair<string, string> pair in dateDirs)
            {
                DateTime date;
                if (!DateTime.TryParseExact(pair.Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out date))
                    continue;

                long bytes;
                int files;
                MeasureDirectory(pair.Key, out bytes, out files);

                result.Add(new CleanItem
                {
                    Name = pair.Value,
                    FullPath = pair.Key,
                    DateText = date.ToString("yyyy-MM-dd"),
                    SizeBytes = bytes,
                    SizeText = FormatSize(bytes),
                    FileCount = files,
                    WillDelete = date.Date < cutoff,
                    StatusText = date.Date < cutoff ? "将删除" : "保留"
                });
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return result;
        }

        static IEnumerable<string> SafeEnumerateDirs(string path)
        {
            try { return Directory.EnumerateDirectories(path).ToArray(); }
            catch { return new string[0]; }
        }

        static void MeasureDirectory(string path, out long bytes, out int files)
        {
            bytes = 0;
            files = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(f).Length;
                        files++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        static bool IsDateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length != 8) return false;
            foreach (char c in name)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        async void Clean_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(mScanRoot))
                {
                    MessageBox.Show("请先点「扫描预览」，确认要删除的目录。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                List<CleanItem> items = lbCleanItems.ItemsSource as List<CleanItem>;
                if (items == null) return;

                List<CleanItem> toDelete = items.Where(i => i.WillDelete).ToList();
                if (toDelete.Count == 0)
                {
                    MessageBox.Show("当前没有需要删除的日期目录。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                long bytes = toDelete.Sum(i => i.SizeBytes);
                StringBuilder preview = new StringBuilder();
                foreach (CleanItem item in toDelete.Take(8)) preview.AppendLine("  " + item.Name + "  " + item.SizeText);
                if (toDelete.Count > 8) preview.AppendLine("  … 等共 " + toDelete.Count + " 个");

                string msg = "将永久删除以下日期目录（不可恢复）：\n\n" +
                             preview +
                             "\n合计约 " + FormatSize(bytes) + "\n根目录：" + mScanRoot +
                             "\n\n确认继续？";

                if (MessageBox.Show(msg, "确认执行清理", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;

                btnScan.IsEnabled = false;
                btnClean.IsEnabled = false;
                txtCleanStatus.Text = "正在删除…";
                SetBusy(true, "正在删除过期目录…");

                mCleanRunner = new ScriptRunner();
                mCleanRunner.Output += line =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        txtCleanStatus.Text = line.Length > 72 ? line.Substring(0, 72) + "…" : line;
                    }));
                };

                string[] args = { "-Root", mScanRoot, "-Days", mScanDays.ToString(CultureInfo.InvariantCulture), "-Execute" };
                int code = await mCleanRunner.RunAsync(
                    EmbeddedScripts.ResolveScript("Clear-OldImages.ps1"), args, CancellationToken.None);

                if (code == 0)
                {
                    MessageBox.Show("清理完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("清理结束，但有失败项（退出码 " + code + "）。\n\n" +
                                    "常见原因：文件正被占用、权限不足、网络中断。\n可以稍后重跑，或到目标机双击 Run-CleanOld.cmd 看详细输出。",
                        "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理过程中出错：\n" + ex.Message, "出错了",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                mCleanRunner = null;
                btnScan.IsEnabled = true;
                SetBusy(false, "就绪");
                await RunScanAsync(); // 删除后刷新列表
            }
        }

        // =====================================================================
        // 页④ 说明 / 打开目录
        // =====================================================================

        void OpenScripts_Click(object sender, RoutedEventArgs e)
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string local = Path.Combine(exeDir, "Scripts");
            string dir = Directory.Exists(local) ? local : EmbeddedScripts.EnsureExtracted();
            OpenFolder(dir);
        }

        void OpenReadme_Click(object sender, RoutedEventArgs e)
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string readme = Path.Combine(exeDir, "README.md");
            if (!File.Exists(readme))
            {
                MessageBox.Show("exe 同目录下没有 README.md。\n\n发布时把 README.md 和 exe 放在一起，或到项目目录查看完整说明。",
                    "没有找到说明文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try { Process.Start(new ProcessStartInfo(readme) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void OpenTemp_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(Path.Combine(Path.GetTempPath(), "ImageShareSetup"));
        }

        void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        /// <summary>复制到剪贴板：剪贴板被别的程序占用时重试 3 次，不弹异常。</summary>
        void SafeCopy(string text, string successMessage)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    if (!string.IsNullOrEmpty(successMessage))
                        MessageBox.Show(successMessage, "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }

            MessageBox.Show("复制失败（剪贴板被其它程序占用）。请手动复制：\n" + text,
                "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
