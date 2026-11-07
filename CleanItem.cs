namespace ImageShareSetup
{
    /// <summary>
    /// 页③清理预览的一行（纯展示模型，扫描结果生成后不再变化）。
    /// WillDelete=true 的目录才会被删除，界面用红色"将删除"标签标出来。
    /// </summary>
    public sealed class CleanItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string DateText { get; set; }
        public long SizeBytes { get; set; }
        public string SizeText { get; set; }
        public int FileCount { get; set; }
        public bool WillDelete { get; set; }
        public string StatusText { get; set; }
    }
}
