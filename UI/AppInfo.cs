namespace OppoPodsManager;

/// <summary>
/// 跨页面共享的应用版本标签。原 MainWindow 的 VersionText 文本在界面拆分后
/// 被关于页展示，同时被日志反馈导出引用，故提升为进程级共享值，避免跨视图访问控件。
/// </summary>
public static class AppInfo
{
    public static string VersionLabel { get; set; } = "v?";

    /// <summary>发布页地址：不再联网检测版本，界面上「获取更新」直接跳转到该地址。</summary>
    public const string ReleasesUrl = "https://github.com/ACLaniakea/OppoPodsManager/releases";
}
