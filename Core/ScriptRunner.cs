using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageShareSetup.Core
{
    /// <summary>
    /// PowerShell 脚本执行器（流式回传输出）。
    ///
    /// 关键点（都是踩过的坑）：
    ///   1. 直接给 powershell.exe 拼参数时，中文/空格/引号很容易被拆坏；
    ///      这里统一走一个固定的 wrapper.ps1，参数原样转发给目标脚本。
    ///   2. 脚本里的中文输出默认按控制台代码页（简体中文是 936/GBK）编码，
    ///      用 UTF-8 去读会变成乱码。wrapper 里先把 Console.OutputEncoding
    ///      设成 UTF-8，父进程再用 UTF-8 解码，两边就对齐了。
    ///   3. 必须同时读 stdout 和 stderr。只读一个的话，另一个缓冲区写满会把
    ///      子进程卡死（经典重定向死锁）。
    ///   4. 不弹黑窗口：CreateNoWindow + UseShellExecute=false。
    /// </summary>
    public sealed class ScriptRunner
    {
        /// <summary>脚本每输出一行就回调一次（在后台线程触发，UI 侧自行切回主线程）。</summary>
        public event Action<string> Output;

        /// <summary>脚本结束时回调退出码（0=成功）。</summary>
        public event Action<int> Finished;

        Process mProcess;
        readonly object mLock = new object();

        public bool IsRunning
        {
            get
            {
                lock (mLock) { return mProcess != null && !mProcess.HasExited; }
            }
        }

        /// <summary>
        /// 异步执行脚本。返回退出码；被取消返回 -1。
        /// </summary>
        public async Task<int> RunAsync(string scriptPath, string[] args, CancellationToken token)
        {
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("找不到脚本：" + scriptPath, scriptPath);

            string wrapper = EnsureWrapper(Path.GetDirectoryName(scriptPath));

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
            };

            // 优先用系统目录下的 powershell.exe；个别精简系统没有时退回 PATH
            if (!File.Exists(psi.FileName)) psi.FileName = "powershell.exe";

            psi.Arguments = BuildArguments(wrapper, scriptPath, args);

            Process process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) Raise(Output, e.Data);
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Raise(Output, e.Data);
            };
            process.Exited += (s, e) =>
            {
                int code;
                try { code = process.ExitCode; }
                catch { code = -2; }
                // 等异步输出读完再结束，避免最后几行日志丢失
                try { process.WaitForExit(3000); } catch { }
                Raise(Finished, code);
                tcs.TrySetResult(code);
            };

            lock (mLock) { mProcess = process; }

            // 取消：直接杀进程树。脚本内部没有长事务，杀掉不会造成半截配置
            // （配置动作都是"建目录/建共享"这类幂等操作，重跑一次即可）。
            using (token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        // 杀进程树：powershell 可能再拉起 icacls/net 等子进程
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                            Arguments = "/PID " + process.Id + " /T /F",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                }
                catch { }
                tcs.TrySetResult(-1);
            }))
            {
                // 日志里回显命令行方便现场排障，但密码必须打码：
                // 用户会把日志复制出来发给我们，明文密码等于直接泄露。
                Raise(Output, "> " + Path.GetFileName(scriptPath) + " " + MaskArgs(args));
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (token.IsCancellationRequested) return -1;

                int exitCode = await tcs.Task.ConfigureAwait(false);
                lock (mLock) { mProcess = null; }
                return exitCode;
            }
        }

        /// <summary>请求停止当前脚本（用于"停止"按钮）。</summary>
        public void Stop()
        {
            Process p;
            lock (mLock) { p = mProcess; }
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                }
            }
            catch { }
        }

        static void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null) return;
            try { handler(value); } catch { }
        }

        /// <summary>
        /// 把参数拼成 Windows 命令行。含空格/引号的参数用双引号包裹，
        /// 反斜杠按 Windows 规则转义（末尾反斜杠必须加倍，否则会把收尾引号转义掉）。
        /// </summary>
        static string BuildArguments(string wrapper, string scriptPath, string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            sb.Append(Quote(wrapper));
            sb.Append(' ');
            sb.Append(Quote(scriptPath));

            if (args != null)
            {
                foreach (string a in args)
                {
                    sb.Append(' ');
                    sb.Append(Quote(a ?? string.Empty));
                }
            }
            return sb.ToString();
        }

        static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";

            bool needQuote = value.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^' }) >= 0;
            if (!needQuote) return value;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') sb.Append("\\\"");
                else sb.Append(c);
            }
            // 末尾反斜杠：在 " 前会转义引号，必须加倍
            if (value.EndsWith("\\", StringComparison.Ordinal)) sb.Append('\\');
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 日志回显用的参数串：-Password 后面的值一律打码。
        /// 其余参数原样显示（现场排障需要看到盘符/共享名）。
        /// </summary>
        static string MaskArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? string.Empty;
                bool isPasswordValue = i > 0 &&
                    string.Equals(args[i - 1], "-Password", StringComparison.OrdinalIgnoreCase);
                sb.Append(isPasswordValue ? "******" : a);
                if (i < args.Length - 1) sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成转发参数用的 wrapper.ps1（内容固定，写在脚本同目录，随临时目录一起消失）。
        ///
        /// 参数转发（V1.0 事故根因）：
        ///   不能写 `& $target @rest`。数组 splatting 是"按位置"传参，`-ShareName`
        ///   这类字符串会被当成第一个位置参数的值，真正的参数名全部错位；
        ///   位置槽一满就报 "找不到接受实际参数 -Password 的位置形式参数"，
        ///   脚本一行都没执行。这里先解析成 hashtable，hashtable splatting 才按名绑定。
        ///
        /// 退出码判定：
        ///   PowerShell 的参数绑定错误是非终止错误，$LASTEXITCODE 为 $null。
        ///   先清空它，再用 `$?` 兜底，避免界面把"没跑起来"显示成"配置成功"（假绿）。
        /// </summary>
        static string EnsureWrapper(string dir)
        {
            string path = Path.Combine(dir, "runner-wrapper.ps1");
            const string content =
                "# 由 ImageShareSetup.exe 自动生成：统一 UTF-8 输出 + 按参数名转发给目标脚本\r\n" +
                "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }\r\n" +
                "$target = $args[0]\r\n" +
                "$h = @{}\r\n" +
                "$i = 1\r\n" +
                "while ($i -lt $args.Count) {\r\n" +
                "    $k = [string]$args[$i]\r\n" +
                "    if (-not $k.StartsWith('-')) { $i++; continue }\r\n" +
                "    $name = $k.Substring(1)\r\n" +
                "    $hasNext = ($i + 1) -lt $args.Count\r\n" +
                "    $next = if ($hasNext) { [string]$args[$i + 1] } else { $null }\r\n" +
                "    # 开关参数没有值；-Password 强制取下一个（密码允许以 - 开头）\r\n" +
                "    if ($hasNext -and ($name -eq 'Password' -or -not $next.StartsWith('-'))) {\r\n" +
                "        $h[$name] = $next\r\n" +
                "        $i += 2\r\n" +
                "    } else {\r\n" +
                "        $h[$name] = $true\r\n" +
                "        $i += 1\r\n" +
                "    }\r\n" +
                "}\r\n" +
                "$global:LASTEXITCODE = $null\r\n" +
                "& $target @h\r\n" +
                "$ok = $?\r\n" +
                "$code = $LASTEXITCODE\r\n" +
                "if ($null -eq $code) { if ($ok) { $code = 0 } else { $code = 1 } }\r\n" +
                "exit $code\r\n";

            // 每次都写：保证 wrapper 内容随程序升级（成本可忽略）
            File.WriteAllText(path, content, new UTF8Encoding(true));
            return path;
        }
    }
}
