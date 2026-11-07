using System;
using System.IO;
using System.Reflection;

namespace ImageShareSetup.Core
{
    /// <summary>
    /// 内嵌脚本释放器。
    ///
    /// 为什么要把 ps1 内嵌进 exe：
    ///   现场部署时只拷一个 ImageShareSetup.exe 就能干活，不会出现"exe 拷了、脚本忘了"
    ///   的低级事故；同时 Scripts 目录下仍保留同一份 ps1，方便高级用户直接双击运行。
    ///
    /// 释放位置：%TEMP%\ImageShareSetup\scripts\&lt;程序版本&gt;\
    ///   · 按版本分目录：升级 exe 后不会继续用旧脚本；
    ///   · 每次启动都覆盖写一遍：即使临时目录被人改坏，也会自动恢复。
    /// </summary>
    public static class EmbeddedScripts
    {
        const string Prefix = "ImageShareSetup.Scripts.";
        static string mCachedDir;

        /// <summary>三个脚本的文件名（与内嵌资源 LogicalName 一一对应）。</summary>
        public static readonly string[] Names =
        {
            "Setup-ImageShare.ps1",
            "Connect-ImageShare.ps1",
            "Clear-OldImages.ps1"
        };

        /// <summary>
        /// 把内嵌脚本释放到临时目录，返回脚本所在目录。
        /// 失败时抛出异常（界面会显示到日志区），不会静默降级。
        /// </summary>
        public static string EnsureExtracted()
        {
            if (!string.IsNullOrEmpty(mCachedDir) && Directory.Exists(mCachedDir))
                return mCachedDir;

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string dir = Path.Combine(Path.GetTempPath(), "ImageShareSetup", "scripts", version);
            Directory.CreateDirectory(dir);

            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in Names)
            {
                string resource = Prefix + name;
                using (Stream s = asm.GetManifestResourceStream(resource))
                {
                    if (s == null)
                        throw new InvalidOperationException("程序内缺少内嵌脚本：" + resource);

                    string target = Path.Combine(dir, name);
                    // FileShare.Read：防止别的进程正在读时写失败；先写 .tmp 再替换更稳
                    string tmp = target + ".tmp";
                    long expected = s.Length;
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        s.CopyTo(fs);
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);

                    // 长度校验：杀毒/同步盘偶尔会拦截或截断写入，长度不符就别让界面拿半个脚本去跑
                    long actual = new FileInfo(target).Length;
                    if (actual != expected)
                        throw new IOException("内嵌脚本释放不完整：" + target +
                            "（期望 " + expected + " 字节，实际 " + actual + " 字节）");
                }
            }

            mCachedDir = dir;
            return dir;
        }

        /// <summary>取某个脚本的完整路径（不存在会先释放）。</summary>
        public static string GetScriptPath(string fileName)
        {
            return Path.Combine(EnsureExtracted(), fileName);
        }

        /// <summary>
        /// 优先使用 exe 同级 Scripts 目录（现场替换脚本可立即生效），
        /// 没有再用内嵌释放出来的副本。这样"临时改一行脚本验证"不用重新编译 exe。
        /// </summary>
        public static string ResolveScript(string fileName)
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(exeDir))
            {
                string local = Path.Combine(exeDir, "Scripts", fileName);
                if (File.Exists(local)) return local;
            }
            return GetScriptPath(fileName);
        }
    }
}
