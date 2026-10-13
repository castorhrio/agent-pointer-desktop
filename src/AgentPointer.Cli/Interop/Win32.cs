namespace AgentPointer.Cli.Interop;

/// <summary>
/// Win32 常量集中在这里，<b>不要让模型凭记忆生成这些数字</b>——全部显式列出。
/// </summary>
internal static class Win32
{
    // ---- 扩展窗口样式 (GetWindowLongPtr GWL_EXSTYLE) ----
    public const int GWL_EXSTYLE = -20;

    /// <summary>窗口分层，用来做逐像素透明。**必需**。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>不显示在任务栏 / Alt-Tab 里。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>点击时不激活窗口。注意：它只阻止激活，<b>不阻止窗口吃掉鼠标消息</b>。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// 在 WS_EX_LAYERED 窗口上使鼠标事件穿透到下层窗口，包括其他进程的窗口。
    /// </summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    // ---- SetWindowPos ----
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ---- ShowWindow ----
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;

    // ---- 窗口消息 ----
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DEVICECHANGE = 0x0219;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_MOUSELEAVE = 0x02A3;

    // ---- WM_NCHITTEST 返回值 ----

    /// <summary>命中客户区：窗口自己吃掉这个点击。</summary>
    public const int HTCLIENT = 1;

    /// <summary>
    /// 命中测试继续询问同线程下层窗口；跨进程穿透使用分层窗口的 WS_EX_TRANSPARENT。
    /// </summary>
    public const int HTTRANSPARENT = -1;

    /// <summary>让 Windows 不激活窗口（配合 WM_MOUSEACTIVATE）。</summary>
    public const int MA_NOACTIVATE = 3;

    // ---- 显示器 ----
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const int MDT_EFFECTIVE_DPI = 0;

    // ---- 窗口查询 ----
    public const uint GW_OWNER = 4;
    public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- WaitForSingleObject ----
    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;

    // ---- 桌面检测 ----
    public const int UOI_NAME = 2;
}
