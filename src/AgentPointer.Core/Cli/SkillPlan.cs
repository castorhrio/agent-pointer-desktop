using System.Text.RegularExpressions;

namespace AgentPointer.Core.Cli;

/// <summary>单个技能文件要执行的动作。</summary>
public enum SkillFileAction
{
    /// <summary>文件缺失，或内容需要更新：写入。</summary>
    Write,

    /// <summary>内容已经完全一致：不写。</summary>
    SkipSame,

    /// <summary>内容不同，且没有 <c>--force</c>：不写，让用户自己决定。</summary>
    SkipDiffers,
}

/// <summary>一个文件的计划结果。</summary>
public sealed record SkillFilePlan(string RelativePath, SkillFileAction Action);

/// <summary>一个技能目录的整体计划结果。</summary>
public sealed record SkillPlanResult(string Status, bool Installed, IReadOnlyList<SkillFilePlan> Files)
{
    /// <summary>取某个文件的动作。</summary>
    public SkillFileAction ActionFor(string relativePath) =>
        Files.First(file => file.RelativePath == relativePath).Action;
}

/// <summary>
/// 纯函数：决定技能目录里哪些文件写、哪些跳过。
///
/// <para>规则：缺失的文件<strong>永远</strong>补上（不需要 <c>--force</c>）；内容差的只是
/// 嵌入的命令行路径（可执行文件搬过家）也算更新，同样不需要 <c>--force</c>；
/// 只有用户真改过内容时才需要 <c>--force</c>。</para>
///
/// <para>状态：<c>installed</c> 全部文件都缺 / <c>updated</c> 补了缺文件或写了已存在的文件 /
/// <c>up-to-date</c> 全部一致 / <c>exists</c> 有内容不同的文件且没给 <c>--force</c> /
/// <c>error</c> 有文件读不出来。</para>
/// </summary>
public static class SkillPlan
{
    /// <summary>技能模板里的命令行占位符，安装时被替换成 exe 的绝对路径。</summary>
    public const string CommandPlaceholder = "__AGENTPOINTER_COMMAND__";

    /// <summary>单个文件的输入：期望内容，以及磁盘现状（内容或读取失败原因）。</summary>
    public sealed record FileInput(
        string RelativePath,
        string DesiredContent,
        bool Exists,
        string? CurrentContent,
        string? ReadError = null);

    /// <summary>算出每个文件的动作与整个目录的状态。</summary>
    public static SkillPlanResult Plan(IReadOnlyList<FileInput> files, bool force)
    {
        if (files.Count == 0) throw new ArgumentException("no skill files given", nameof(files));

        if (files.Any(file => file.ReadError is not null))
        {
            return new SkillPlanResult(
                "error", false,
                files.Select(file => new SkillFilePlan(file.RelativePath, SkillFileAction.SkipDiffers)).ToArray());
        }

        var plans = new List<SkillFilePlan>(files.Count);
        var existed = 0;

        foreach (var file in files)
        {
            if (!file.Exists)
            {
                // 缺文件永远补上，不需要 --force
                plans.Add(new SkillFilePlan(file.RelativePath, SkillFileAction.Write));
                continue;
            }

            existed++;
            var same = Same(file.CurrentContent, file.DesiredContent);
            var movedOnly = !same && Same(
                NormalizeInvocation(file.CurrentContent ?? string.Empty),
                NormalizeInvocation(file.DesiredContent));

            plans.Add(new SkillFilePlan(file.RelativePath,
                same ? SkillFileAction.SkipSame
                : force || movedOnly ? SkillFileAction.Write
                : SkillFileAction.SkipDiffers));
        }

        var status =
            plans.Any(plan => plan.Action == SkillFileAction.SkipDiffers) ? "exists"
            : plans.All(plan => plan.Action == SkillFileAction.SkipSame) ? "up-to-date"
            : existed == 0 ? "installed"
            : "updated";

        return new SkillPlanResult(status, status is "installed" or "updated" or "up-to-date", plans);
    }

    /// <summary>把 <c>&amp; '…exe'</c>（dotnet 后面还会跟一个 dll 路径）统一成一个记号。</summary>
    public static string NormalizeInvocation(string content) =>
        InvocationPattern.Replace(content.Replace("\r\n", "\n"), "& <command>");

    private static readonly Regex InvocationPattern = new(@"&\s*'[^']*'(?:\s*'[^']*')?", RegexOptions.Compiled);

    /// <summary>换行差异不算差异：git autocrlf 之后仍应判为一致。</summary>
    private static bool Same(string? left, string? right) =>
        (left ?? string.Empty).Replace("\r\n", "\n") == (right ?? string.Empty).Replace("\r\n", "\n");
}
