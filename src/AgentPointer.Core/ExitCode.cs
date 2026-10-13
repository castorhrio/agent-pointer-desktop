namespace AgentPointer.Core;

/// <summary>
/// 退出码。0/2/3/4 沿用上游；5/6 区分未支持功能与内部错误。
/// </summary>
public static class ExitCode
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>参数错误（未知参数、取值非法、目标参数缺失）。</summary>
    public const int BadInput = 2;

    /// <summary>目标未找到（窗口不存在、元素匹配不上、坐标落在屏幕外）。</summary>
    public const int TargetNotFound = 3;

    /// <summary>权限缺失，或运行在无法绘制覆盖层的桌面上（锁屏 / UAC 安全桌面）。</summary>
    public const int PermissionMissing = 4;

    /// <summary>功能在 Windows 版中尚不支持。</summary>
    public const int Unsupported = 5;

    /// <summary>未预期的内部错误或后台启动失败。</summary>
    public const int InternalError = 6;
}
