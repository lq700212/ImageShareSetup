using System;
using System.Collections.Generic;
using System.IO;

namespace ImageShareSetup.Core
{
    /// <summary>一块可用于存图的本地磁盘（已过滤系统盘/非 NTFS/未就绪）。</summary>
    public sealed class DiskInfo
    {
        public string Letter { get; set; }
        public string Label { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        /// <summary>盘符文本，例如 "D:"。</summary>
        public string LetterText
        {
            get { return Letter + ":"; }
        }

        /// <summary>卷标为空时显示"无卷标"，避免界面出现空白行。</summary>
        public string LabelText
        {
            get { return string.IsNullOrWhiteSpace(Label) ? "无卷标" : Label; }
        }

        public double TotalGB
        {
            get { return Math.Round(TotalBytes / 1024.0 / 1024.0 / 1024.0, 1); }
        }

        public double FreeGB
        {
            get { return Math.Round(FreeBytes / 1024.0 / 1024.0 / 1024.0, 1); }
        }

        /// <summary>已用比例 0~1，给界面的占用条用。</summary>
        public double UsedRatio
        {
            get
            {
                if (TotalBytes <= 0) return 0;
                double used = (TotalBytes - FreeBytes) / (double)TotalBytes;
                if (used < 0) used = 0;
                if (used > 1) used = 1;
                return used;
            }
        }

        public string SummaryText
        {
            get { return string.Format("{0} GB 容量 · 剩余 {1} GB", TotalGB, FreeGB); }
        }

        /// <summary>占用条下方的文字，例如 "已用 62%"。</summary>
        public string UsedPercentText
        {
            get { return "已用 " + Math.Round(UsedRatio * 100) + "%"; }
        }
    }

    /// <summary>
    /// 本地磁盘扫描。
    ///
    /// 过滤规则与 Setup-ImageShare.ps1 保持一致：
    ///   · 只认固定磁盘（排除 U 盘/移动硬盘/光驱/网络盘）；
    ///   · 必须已就绪、文件系统是 NTFS（共享 + ACL 依赖 NTFS）；
    ///   · 排除系统盘（不允许把存图目录建在 C:）。
    /// 这样界面里列出来的盘，脚本一定能用，不会出现"界面能选、脚本报错"。
    /// </summary>
    public static class DiskScanner
    {
        public static List<DiskInfo> Scan()
        {
            List<DiskInfo> list = new List<DiskInfo>();
            string sysLetter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 1);

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return list; }

            foreach (DriveInfo d in drives)
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (!d.IsReady) continue;
                    if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;

                    string letter = d.Name.Substring(0, 1).ToUpperInvariant();
                    if (letter == sysLetter.ToUpperInvariant()) continue;

                    DiskInfo info = new DiskInfo
                    {
                        Letter = letter,
                        Label = SafeLabel(d),
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.AvailableFreeSpace
                    };
                    list.Add(info);
                }
                catch
                {
                    // 单块盘读不到信息（被占用/权限/坏道）不能拖垮整个列表
                }
            }

            // 容量大的排前面：现场数据盘通常是最大的那块
            list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
            return list;
        }

        static string SafeLabel(DriveInfo d)
        {
            try { return d.VolumeLabel ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
