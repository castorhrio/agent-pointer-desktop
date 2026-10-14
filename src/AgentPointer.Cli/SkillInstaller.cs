using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Protocol;
using Microsoft.Win32;

namespace AgentPointer.Cli;

/// <summary>
/// 安装 Claude Code / Codex 的技能文件，并可把 CLI 所在目录加进用户 PATH。
///
/// <para>缺的文件永远补上；内容只差嵌入的命令行路径（exe 搬过家）算更新，也不需要 <c>--force</c>。
/// 只有用户真改过内容时才<strong>不覆盖</strong>，要覆盖得显式给 <c>--force</c>：
/// 静默盖掉用户改过的文件比"没装上"更糟。判断逻辑在 <see cref="SkillPlan"/> 里。</para>
/// </summary>
internal static class SkillInstaller
{
    /// <summary>用户环境变量键（用户 PATH 就在里面）。</summary>
    private const string UserEnvironmentKey = "Environment";

    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;
    private const int BroadcastTimeoutMs = 5000;
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    internal static int Install(PointerCommand command)
    {
        var json = command.Json;
        var dryRun = command.DryRun;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) throw new IOException(Lang.T(
            "无法确定用户目录。", "Could not determine the user profile directory."));

        // --claude / --codex 都是"只装哪个"；两个都不给才是两个都装。
        var wantClaude = command.InstallClaude || !command.InstallCodex;
        var wantCodex = command.InstallCodex || !command.InstallClaude;

        var directories = new List<(string Agent, string Directory)>();
        if (wantClaude) directories.Add(("Claude Code", Path.Combine(profile, ".claude", "skills", "agentpointer")));
        if (wantCodex) directories.Add(("Codex", Path.Combine(profile, ".agents", "skills", "agentpointer")));

        var executable = Environment.ProcessPath ?? throw new IOException(Lang.T(
            "无法确定程序路径。", "Could not determine the program path."));
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var invocation = "& " + Quote(executable);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            invocation += " " + Quote(typeof(SkillInstaller).Assembly.Location);

        var files = new[]
        {
            new SkillFile("SKILL.md", ReadResource("AgentPointer.Skill.md").Replace("__AGENTPOINTER_COMMAND__", invocation)),
            new SkillFile(Path.Combine("agents", "openai.yaml"), ReadResource("AgentPointer.Skill.openai.yaml")),
        };

        var reports = new List<SkillReport>(directories.Count);
        foreach (var directory in directories)
        {
            var (inputs, readError) = ReadInputs(directory.Directory, files);
            var plan = SkillPlan.Plan(inputs, command.Force);

            if (plan.Status == "error")
            {
                // 读不出来就报权限错误：不能在不确定的情况下覆盖，更不能假装装好了
                Console.Error.WriteLine(Lang.T(
                    $"读不了技能目录 {directory.Directory}：{readError}",
                    $"Could not read the skill directory {directory.Directory}: {readError}"));
                return Core.ExitCode.PermissionMissing;
            }

            if (!dryRun)
            {
                foreach (var file in files)
                {
                    if (plan.ActionFor(file.RelativePath) != SkillFileAction.Write) continue;

                    try
                    {
                        Write(directory.Directory, file.RelativePath, file.Content);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var path = Path.Combine(directory.Directory, file.RelativePath);
                        Console.Error.WriteLine(Lang.T(
                            $"写不了技能文件 {path}：{ex.Message}",
                            $"Could not write the skill file {path}: {ex.Message}"));
                        return Core.ExitCode.PermissionMissing;
                    }
                }
            }

            reports.Add(new SkillReport(
                directory.Agent,
                directory.Directory,
                plan.Installed && !dryRun,
                plan.Status == "up-to-date",
                plan.Status,
                null));
        }

        var skills = reports.ToArray();

        var pathStatus = "skipped";
        if (command.AddPath)
        {
            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var (merged, changed) = MergeUserPath(directory);
                pathStatus = changed ? "added" : "already";

                if (changed && !dryRun)
                {
                    WriteUserPath(merged);
                    BroadcastEnvironmentChange();
                }
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                dryRun,
                // 顶层 installed 只有在每个目录都真的写上/已是最新时才为 true
                installed = !dryRun && skills.All(skill => skill.status is "installed" or "updated" or "up-to-date"),
                directories = directories.Select(d => d.Directory),
                skills,
                path = pathStatus,
            }));
        }
        else
        {
            foreach (var line in Describe(skills, command, pathStatus, dryRun)) Console.WriteLine(line);
        }

        return Core.ExitCode.Ok;
    }

    /// <summary>要写入的技能文件（相对技能目录）。</summary>
    private sealed record SkillFile(string RelativePath, string Content);

    /// <summary>每个技能目标的探测结果。字段名刻意与既有 JSON 字段一致。</summary>
    private sealed record SkillReport(
        string agent, string directory, bool installed, bool current, string status, string? error);

    /// <summary>读每个文件的现状（内容或读取失败原因），交给 <see cref="SkillPlan"/> 决定写不写。</summary>
    private static (SkillPlan.FileInput[] Inputs, string? Error) ReadInputs(string directory, SkillFile[] files)
    {
        var inputs = new SkillPlan.FileInput[files.Length];
        string? error = null;

        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            var path = Path.Combine(directory, file.RelativePath);
            if (!File.Exists(path))
            {
                inputs[index] = new SkillPlan.FileInput(file.RelativePath, file.Content, false, null);
                continue;
            }

            try
            {
                inputs[index] = new SkillPlan.FileInput(
                    file.RelativePath, file.Content, true, File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不动就报权限错误：宁可让调用方失败，也不要在不确定的情况下覆盖
                error ??= ex.Message;
                inputs[index] = new SkillPlan.FileInput(file.RelativePath, file.Content, true, null, ex.Message);
            }
        }

        return (inputs, error);
    }

    private static void Write(string directory, string relativePath, string content)
    {
        var destination = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>文本输出：每个目标的实际/计划状态，以及 PATH 结果与下一步该做什么。</summary>
    private static IEnumerable<string> Describe(
        SkillReport[] skills, PointerCommand command, string pathStatus, bool dryRun)
    {
        if (dryRun) yield return Lang.T(
            "dry-run：以下技能路径没有写入。", "dry-run: no skill path was written.");

        foreach (var skill in skills)
        {
            var where = $"{skill.agent} → {skill.directory}";
            yield return skill.status switch
            {
                "installed" => dryRun
                    ? Lang.T($"将安装：{where}", $"will install: {where}")
                    : Lang.T($"技能已安装：{where}", $"skill installed: {where}"),
                "updated" => dryRun
                    ? Lang.T($"将覆盖：{where}", $"will overwrite: {where}")
                    : Lang.T($"技能已更新：{where}", $"skill updated: {where}"),
                "up-to-date" => Lang.T($"已是最新：{where}", $"already up to date: {where}"),
                _ => dryRun
                    ? Lang.T(
                        $"内容不同的文件将跳过，缺失的仍会写入：{where}（要覆盖需 --force）",
                        $"files that differ will be skipped, missing ones are still written: {where} (rerun with --force to overwrite)")
                    : Lang.T(
                        $"内容不同的文件已跳过，缺失的已写入：{where}（确认要覆盖时重跑并加 --force）",
                        $"files that differ were skipped, missing ones were written: {where} (rerun with --force to overwrite)"),
            };
        }

        if (!command.AddPath) yield break;

        yield return pathStatus switch
        {
            "added" => dryRun
                ? Lang.T(
                    "将把本程序所在目录加入用户 PATH；真正执行后请新开一个终端。",
                    "will add the program's directory to the user PATH; after a real run, open a new terminal.")
                : Lang.T(
                    "已把本程序所在目录加入用户 PATH，请新开一个终端让新终端看到它。",
                    "added the program's directory to the user PATH; open a new terminal to see it."),
            "already" => Lang.T(
                "用户 PATH 里已经有本程序所在目录，未重复添加。",
                "the user PATH already contains the program's directory, nothing was added."),
            _ => Lang.T("未修改用户 PATH。", "the user PATH was not changed."),
        };
    }

    /// <summary>
    /// 读 HKCU\Environment 的 Path 并算出合并结果。
    /// 用 <c>DoNotExpandEnvironmentNames</c> 读，<c>%USERPROFILE%</c> 这类变量才能原样保留。
    /// </summary>
    private static (string newValue, bool changed) MergeUserPath(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: false)
            ?? throw new IOException(Lang.T(
                @"读不到用户环境变量键 HKCU\Environment。",
                @"Could not read the user environment key HKCU\Environment."));

        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? string.Empty;
        return PathList.Add(current, directory);
    }

    /// <summary>写回 PATH，并保留原有的值类型：REG_EXPAND_SZ 不能被降级成 REG_SZ，否则变量不再展开。</summary>
    private static void WriteUserPath(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserEnvironmentKey, writable: true)
            ?? throw new IOException(Lang.T(
                @"写不了用户环境变量键 HKCU\Environment。",
                @"Could not write the user environment key HKCU\Environment."));

        var kind = RegistryValueKind.ExpandString;
        try
        {
            if (key.GetValueKind("Path") == RegistryValueKind.String) kind = RegistryValueKind.String;
        }
        catch (Exception)
        {
            // 值不存在时 GetValueKind 会抛；按 REG_EXPAND_SZ 新建即可
        }

        key.SetValue("Path", value, kind);
    }

    /// <summary>
    /// 广播 WM_SETTINGCHANGE，让已经开着的 Explorer / 宿主进程重新读环境变量。
    /// <c>SMTO_ABORTIFHUNG</c> + 5 秒超时，避免被某个卡死的窗口吊住。
    /// </summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
                SmtoAbortIfHung, BroadcastTimeoutMs, out _);
        }
        catch (Exception)
        {
            // 广播失败不影响已经写好的 PATH
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private static string ReadResource(string name)
    {
        using var resource = typeof(SkillInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new IOException(Lang.T(
                "安装包缺少技能资源：" + name, "The package is missing the skill resource: " + name));
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
