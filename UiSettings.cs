using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ImageShareSetup
{
    /// <summary>
    /// 轻量本地设置（主题 + 上次填写的目标机信息）。
    ///
    /// 为什么不写 exe 同目录：
    ///   现场可能把工具放在只读目录/共享盘，写不进去；放 %APPDATA% 永远有权限。
    /// 为什么不存密码：
    ///   明文密码只应存在于目标机生成的"连接信息.txt"里，用完删除；
    ///   源机凭据由 Windows 凭据管理器（cmdkey）保管，不需要重复存明文。
    /// </summary>
    public static class UiSettings
    {
        static readonly Dictionary<string, string> mValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static bool DarkTheme = true;
        public static string Server = "";
        public static string Share = "VisionImages";
        public static string User = "visionshare";
        public static string CleanRoot = "";
        public static int KeepDays = 30;

        static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ImageShareSetup");
                return Path.Combine(dir, "ui.ini");
            }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    mValues[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                DarkTheme = GetBool("Theme.Dark", true);
                Server = Get("Server", "");
                Share = Get("Share", "VisionImages");
                User = Get("User", "visionshare");
                CleanRoot = Get("CleanRoot", "");
                KeepDays = GetInt("CleanDays", 30);
            }
            catch
            {
                // 设置文件坏了不影响主流程，按默认值继续
            }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# ImageShareSetup 本地设置（删掉本文件即恢复默认）");
                sb.AppendLine("Theme.Dark=" + (DarkTheme ? "1" : "0"));
                sb.AppendLine("Server=" + (Server ?? ""));
                sb.AppendLine("Share=" + (Share ?? ""));
                sb.AppendLine("User=" + (User ?? ""));
                sb.AppendLine("CleanRoot=" + (CleanRoot ?? ""));
                sb.AppendLine("CleanDays=" + KeepDays);

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch
            {
                // 设置存不下不是致命问题（只影响下次默认值），静默忽略
            }
        }

        static string Get(string key, string fallback)
        {
            string v;
            return mValues.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        static bool GetBool(string key, bool fallback)
        {
            string v;
            if (!mValues.TryGetValue(key, out v)) return fallback;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        static int GetInt(string key, int fallback)
        {
            string v;
            int n;
            if (mValues.TryGetValue(key, out v) && int.TryParse(v, out n)) return n;
            return fallback;
        }
    }
}
