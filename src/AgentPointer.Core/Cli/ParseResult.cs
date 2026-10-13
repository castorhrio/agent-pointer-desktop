using AgentPointer.Core.Protocol;

namespace AgentPointer.Core.Cli;

/// <summary>
/// 解析结果。永远不向调用方抛异常——错误一律通过退出码和 <see cref="Error"/> 表达。
/// </summary>
/// <remarks>
/// 这里的 <c>ExitCode</c> 属性会遮蔽同名的 <see cref="Core.ExitCode"/> 静态类，
/// 因此类内部一律使用全限定的 <c>Core.ExitCode.X</c>。
/// </remarks>
public sealed record ParseResult
{
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public PointerCommand? Command { get; init; }
    public string? Error { get; init; }
    public bool HelpRequested { get; init; }
    public bool VersionRequested { get; init; }

    public static ParseResult Success(PointerCommand command) =>
        new() { Ok = true, ExitCode = Core.ExitCode.Ok, Command = command };

    public static ParseResult Failure(int exitCode, string error) =>
        new() { Ok = false, ExitCode = exitCode, Error = error };

    public static ParseResult BadInput(string error) => Failure(Core.ExitCode.BadInput, error);

    /// <summary>
    /// 功能在 Windows 版尚未实现（<c>--follow</c> / <c>--png</c>）。退出码 5，
    /// 调用方据此去掉参数重试，而不是以为它已经生效。
    /// </summary>
    public static ParseResult Unsupported(string error) => Failure(Core.ExitCode.Unsupported, error);

    public static ParseResult Help() =>
        new() { Ok = true, ExitCode = Core.ExitCode.Ok, HelpRequested = true };

    public static ParseResult Version() =>
        new() { Ok = true, ExitCode = Core.ExitCode.Ok, VersionRequested = true };
}
