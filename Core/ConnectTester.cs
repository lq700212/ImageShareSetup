using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;

namespace ImageShareSetup.Core
{
    public enum StepState
    {
        Pending,
        Running,
        Ok,
        Warn,
        Fail
    }

    /// <summary>检测步骤：界面按 State 上色/换图标，Detail 显示具体原因或数字。</summary>
    public sealed class TestStep : INotifyPropertyChanged
    {
        string mDetail = "等待检测";
        StepState mState = StepState.Pending;

        public TestStep(string title)
        {
            Title = title;
        }

        public string Title { get; private set; }

        public string Detail
        {
            get { return mDetail; }
            set
            {
                if (mDetail == value) return;
                mDetail = value;
                OnPropertyChanged("Detail");
            }
        }

        public StepState State
        {
            get { return mState; }
            set
            {
                if (mState == value) return;
                mState = value;
                OnPropertyChanged("State");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class ConnectTestResult
    {
        public bool Success { get; set; }
        public string UncPath { get; set; }
        public double SpeedMBps { get; set; }
        public double FreeGB { get; set; }
        public double TotalGB { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 源机连接测试（纯 C# 实现，不依赖 PowerShell）。
    ///
    /// 为什么界面里这部分不用脚本：
    ///   · 步骤要实时反映在界面上（每步变绿/变红），脚本的一整段输出不好拆；
    ///   · 源机可能没装 PowerShell 执行策略放行，C# 直连更稳；
    ///   · 命令行脚本（Connect-ImageShare.ps1）仍保留给现场双击使用，两边逻辑一致。
    ///
    /// 检测顺序（和脚本完全一致）：
    ///   445 端口 → cmdkey 保存凭据 → 访问共享 → 写 8MB → 读回校验 → 剩余空间
    /// </summary>
    public static class ConnectTester
    {
        public const int DefaultSizeMB = 8;

        /// <summary>把用户输入的 "\\192.168.1.10\VisionImages" 拆成主机名 + 共享名。</summary>
        public static void ParseServer(string input, out string server, out string share)
        {
            server = (input ?? string.Empty).Trim();
            share = string.Empty;

            if (server.StartsWith("\\\\", StringComparison.Ordinal))
            {
                string[] parts = server.TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) server = parts[0];
                if (parts.Length > 1) share = parts[1];
            }
        }

        public static string BuildUnc(string server, string share)
        {
            return "\\\\" + server.Trim('\\') + "\\" + share.Trim('\\');
        }

        /// <summary>
        /// 执行完整检测。steps 由界面传入（固定 6 项），本方法只更新它们的 State/Detail。
        /// </summary>
        public static Task<ConnectTestResult> RunAsync(
            string server, string share, string user, string password,
            TestStep[] steps, int sizeMB, CancellationToken token)
        {
            return Task.Run(() => RunCore(server, share, user, password, steps, sizeMB, token), token);
        }

        static ConnectTestResult RunCore(
            string server, string share, string user, string password,
            TestStep[] steps, int sizeMB, CancellationToken token)
        {
            ConnectTestResult result = new ConnectTestResult();
            string unc = BuildUnc(server, share);
            result.UncPath = unc;

            if (sizeMB < 1) sizeMB = DefaultSizeMB;
            if (sizeMB > 256) sizeMB = 256;

            string testFile = Path.Combine(unc, ".imageshare_write_test.bin");

            // ---- 1. 445 端口 ----------------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 0, StepState.Running, "正在连接 " + server + ":445 …");
            if (!ProbeTcp(server, 445, 3000))
            {
                Set(steps, 0, StepState.Fail, "连不上 " + server + ":445");
                MarkSkipped(steps, 1);
                result.Message = "网络不通或目标机没开文件共享。请检查：两台机器是否同一网段、目标机防火墙是否放行 445、目标机 IP 是否变了。";
                return result;
            }
            Set(steps, 0, StepState.Ok, server + ":445 可达");

            // ---- 2. cmdkey 保存凭据 --------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 1, StepState.Running, "保存到 Windows 凭据管理器 …");
            CmdKeyDelete(server); // 先删旧的：同一 IP 换过密码时旧凭据会一直生效
            int rc = CmdKeyAdd(server, user, password);
            if (rc != 0)
            {
                Set(steps, 1, StepState.Fail, "cmdkey 失败（退出码 " + rc + "）");
                MarkSkipped(steps, 2);
                result.Message = "无法保存凭据。请确认账号密码没有特殊字符问题，或手动运行 cmdkey /add:" + server + " /user:" + user;
                return result;
            }
            Set(steps, 1, StepState.Ok, "凭据已保存（重启后仍有效）");

            // ---- 3. 访问共享 ----------------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 2, StepState.Running, "打开 " + unc + " …");
            string accessError;
            if (!CanAccess(unc, out accessError))
            {
                Set(steps, 2, StepState.Fail, accessError);
                MarkSkipped(steps, 3);
                CmdKeyDelete(server); // 错误凭据不能留着，否则存图软件也会跟着登录失败
                result.Message = "能连上端口但进不去共享。常见原因：共享名拼错、账号/密码不对、目标机共享被删、NTFS 权限没给。" +
                                 "可回目标机重跑 Run-Repair.cmd，或在资源管理器地址栏直接输入 " + unc + " 验证。";
                return result;
            }
            Set(steps, 2, StepState.Ok, "共享目录可访问");

            // ---- 4. 写入 8MB ----------------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 3, StepState.Running, "写入 " + sizeMB + " MB 测试文件 …");
            double speed = 0;
            string writeError;
            if (!WriteTest(unc, sizeMB, out speed, out writeError, token))
            {
                Set(steps, 3, StepState.Fail, writeError);
                MarkSkipped(steps, 4);
                TryDelete(testFile);
                CmdKeyDelete(server);
                result.Message = "能进目录但写不进去。通常是 NTFS 权限只给了“读取”、目标盘满了或变成只读。";
                return result;
            }
            Set(steps, 3, StepState.Ok, string.Format("写入正常，约 {0:0.0} MB/s", speed));

            // ---- 5. 读回校验 ----------------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 4, StepState.Running, "读回并校验 …");
            string readError;
            if (!ReadBackTest(testFile, sizeMB, out readError))
            {
                Set(steps, 4, StepState.Fail, readError);
                MarkSkipped(steps, 5);
                TryDelete(testFile);
                result.Message = "写入成功但读回校验失败，硬盘/网络可能有坏块或不稳定。";
                return result;
            }
            TryDelete(testFile);
            Set(steps, 4, StepState.Ok, "读回一致，测试文件已删除");

            // ---- 6. 剩余空间 ----------------------------------------------------
            if (token.IsCancellationRequested) return Cancelled(result);
            Set(steps, 5, StepState.Running, "读取共享剩余空间 …");
            ulong free, total, totalFree;
            if (GetDiskFreeSpaceEx(unc, out free, out total, out totalFree))
            {
                result.FreeGB = Math.Round(free / 1024.0 / 1024.0 / 1024.0, 1);
                result.TotalGB = Math.Round(total / 1024.0 / 1024.0 / 1024.0, 1);
                if (result.FreeGB < 50)
                    Set(steps, 5, StepState.Warn, string.Format("剩余 {0} GB / 总 {1} GB，空间偏少", result.FreeGB, result.TotalGB));
                else
                    Set(steps, 5, StepState.Ok, string.Format("剩余 {0} GB / 总 {1} GB", result.FreeGB, result.TotalGB));
            }
            else
            {
                Set(steps, 5, StepState.Warn, "剩余空间读取失败（不影响使用）");
            }

            result.Success = true;
            result.SpeedMBps = speed;
            result.Message = "连接正常，可以开始存图。";
            return result;
        }

        static ConnectTestResult Cancelled(ConnectTestResult result)
        {
            result.Message = "已取消";
            return result;
        }

        /// <summary>前面的步骤失败后，把后续步骤标成"未执行"，避免用户以为是卡住了。</summary>
        static void MarkSkipped(TestStep[] steps, int startIndex)
        {
            if (steps == null) return;
            for (int i = startIndex; i < steps.Length; i++)
            {
                if (steps[i].State == StepState.Pending)
                    steps[i].Detail = "未执行（前面的步骤失败）";
            }
        }

        static void Set(TestStep[] steps, int index, StepState state, string detail)
        {
            if (steps == null || index < 0 || index >= steps.Length) return;
            steps[index].State = state;
            steps[index].Detail = detail;
        }

        // ---------------------------------------------------------------------
        // 基础能力
        // ---------------------------------------------------------------------

        /// <summary>TCP 端口探测（BeginConnect + WaitOne，兼容 .NET 4.7.2，不受 DNS 卡死影响）。</summary>
        public static bool ProbeTcp(string host, int port, int timeoutMs)
        {
            System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                client.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        /// <summary>访问 UNC 共享目录（空目录也算成功）。</summary>
        public static bool CanAccess(string unc, out string error)
        {
            error = null;
            try
            {
                using (System.Collections.Generic.IEnumerator<string> e =
                       Directory.EnumerateFileSystemEntries(unc).GetEnumerator())
                {
                    e.MoveNext(); // 空目录 MoveNext 返回 false，但不会抛异常
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "拒绝访问（账号/密码不对，或共享、NTFS 权限没给够）";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                error = "找不到共享（共享名拼错，或目标机共享被删）";
                return false;
            }
            catch (IOException ex)
            {
                // Windows 网络错误码在 HResult 低 16 位：67=找不到网络名，53=找不到网络路径，5=拒绝访问
                int code = ex.HResult & 0xFFFF;
                if (code == 53)
                    error = "找不到网络路径（目标机没开机、IP 不对或网络不通）";
                else if (code == 67)
                    error = "找不到共享名（共享名拼错，或目标机上的共享还没建/被删了）";
                else if (code == 5)
                    error = "拒绝访问（账号或密码不正确，或共享/NTFS 权限没给够）";
                else if (code == 86 || code == 1326)
                    error = "登录失败：账号或密码不正确";
                else
                    error = "访问失败：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "访问失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 顺序写入测试：sizeMB 兆，256KB 一块。
        /// 返回 true 时 speed 为写入速度（MB/s，含落盘 flush 时间，比纯内存缓冲更接近现场）。
        /// </summary>
        public static bool WriteTest(string unc, int sizeMB, out double speed, out string error, CancellationToken token)
        {
            speed = 0;
            error = null;

            const int blockSize = 256 * 1024;
            byte[] block = new byte[blockSize];
            // 固定种子：测试只关心"能不能写、多快"，不需要密码学随机
            new Random(20261106).NextBytes(block);

            int blocks = (int)Math.Ceiling(sizeMB * 1024.0 * 1024.0 / blockSize);
            string file = Path.Combine(unc, ".imageshare_write_test.bin");

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (FileStream fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, blockSize))
                {
                    for (int i = 0; i < blocks; i++)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        fs.Write(block, 0, block.Length);
                    }
                    fs.Flush(true); // 真正刷到目标盘，测速才准
                }
                sw.Stop();

                double mb = blocks * (double)blockSize / 1024.0 / 1024.0;
                speed = sw.Elapsed.TotalSeconds > 0 ? Math.Round(mb / sw.Elapsed.TotalSeconds, 1) : 0;

                long length = new FileInfo(file).Length;
                if (length != (long)blocks * blockSize)
                {
                    error = "写入长度不一致（期望 " + (long)blocks * blockSize + "，实际 " + length + "）";
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                error = "已取消";
                return false;
            }
            catch (Exception ex)
            {
                error = "写入失败：" + ex.Message;
                return false;
            }
            finally
            {
                // 失败时也要尽量删掉测试文件，别把客户盘弄脏
                if (error != null) TryDelete(file);
            }
        }

        /// <summary>读回校验：逐块与写入时的固定内容比较（8MB 全量比较，几十毫秒）。</summary>
        public static bool ReadBackTest(string file, int sizeMB, out string error)
        {
            error = null;
            const int blockSize = 256 * 1024;
            byte[] expected = new byte[blockSize];
            new Random(20261106).NextBytes(expected);

            int blocks = (int)Math.Ceiling(sizeMB * 1024.0 * 1024.0 / blockSize);
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, blockSize))
                {
                    byte[] actual = new byte[blockSize];
                    for (int i = 0; i < blocks; i++)
                    {
                        int read = 0;
                        while (read < blockSize)
                        {
                            int n = fs.Read(actual, read, blockSize - read);
                            if (n <= 0) { error = "读回长度不足"; return false; }
                            read += n;
                        }
                        for (int j = 0; j < blockSize; j++)
                        {
                            if (actual[j] != expected[j])
                            {
                                error = "读回内容与写入不一致（第 " + (i + 1) + " 块）";
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "读回失败：" + ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // cmdkey（Windows 凭据管理器）
        // ---------------------------------------------------------------------

        public static int CmdKeyAdd(string server, string user, string password)
        {
            return RunHidden("cmdkey.exe",
                "/add:" + QuoteArg(server) + " /user:" + QuoteArg(user) + " /pass:" + QuoteArg(password));
        }

        public static int CmdKeyDelete(string server)
        {
            return RunHidden("cmdkey.exe", "/delete:" + QuoteArg(server));
        }

        static string QuoteArg(string value)
        {
            value = value ?? string.Empty;
            if (value.Length == 0) return "\"\"";
            // 只对含空格/引号的加引号，避免 cmdkey 把密码里的 @ 等当成特殊字符
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        static int RunHidden(string exe, string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        // ---------------------------------------------------------------------
        // 剩余空间（DriveInfo 对 UNC 无效，必须走 Win32）
        // ---------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        public static bool GetFreeSpace(string path, out double freeGB, out double totalGB)
        {
            freeGB = 0;
            totalGB = 0;
            ulong free, total, totalFree;
            if (!GetDiskFreeSpaceEx(path, out free, out total, out totalFree)) return false;
            freeGB = Math.Round(free / 1024.0 / 1024.0 / 1024.0, 1);
            totalGB = Math.Round(total / 1024.0 / 1024.0 / 1024.0, 1);
            return true;
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }
    }
}
