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
/// <para>默认<strong>不覆盖</strong>已存在且内容不同的文件：目录可能是用户改过的，
/// 静默盖掉比"没装上"更糟。要覆盖得显式给 <c>--force</c>。</para>
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
        if (string.IsNullOrWhiteSpace(profile)) throw new IOException("无法确定用户目录。");

        // --claude / --codex 都是"只装哪个"；两个都不给才是两个都装。
        var wantClaude = command.InstallClaude || !command.InstallCodex;
        var wantCodex = command.InstallCodex || !command.InstallClaude;

        var directories = new List<(string Agent, string Directory)>();
        if (wantClaude) directories.Add(("Claude Code", Path.Combine(profile, ".claude", "skills", "agentpointer")));
        if (wantCodex) directories.Add(("Codex", Path.Combine(profile, ".agents", "skills", "agentpointer")));

        var executable = Environment.ProcessPath ?? throw new IOException("无法确定程序路径。");
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var invocation = "& " + Quote(executable);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            invocation += " " + Quote(typeof(SkillInstaller).Assembly.Location);

        var files = new[]
        {
            new SkillFile("SKILL.md", ReadResource("AgentPointer.Skill.md").Replace("__AGENTPOINTER_COMMAND__", invocation)),
            new SkillFile(Path.Combine("agents", "openai.yaml"), ReadResource("AgentPointer.Skill.openai.yaml")),
        };

        var skills = directories.Select(directory =>
        {
            var (status, installed, current, error) = Inspect(directory.Directory, files, command.Force);

            // 只有"新装"和"强制覆盖"才落盘；up-to-date 与 exists 都保持文件原样
            if (!dryRun && status is "installed" or "updated")
            {
                foreach (var file in files)
                {
                    Write(directory.Directory, file.RelativePath, file.Content);
                }
            }

            return new SkillReport(directory.Agent, directory.Directory, installed, current, status, error);
        }).ToArray();

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
                installed = !dryRun,
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

    /// <summary>
    /// 探测一个目标目录的计划状态：
    /// <c>installed</c> 文件都不在 / <c>up-to-date</c> 内容完全一致 /
    /// <c>exists</c> 有差异且没给 --force / <c>updated</c> 有差异且给了 --force。
    /// </summary>
    private static (string Status, bool Installed, bool Current, string? Error) Inspect(
        string directory, SkillFile[] files, bool force)
    {
        try
        {
            var installed = File.Exists(Path.Combine(directory, "SKILL.md"));
            var missing = 0;
            var identical = 0;

            foreach (var file in files)
            {
                var path = Path.Combine(directory, file.RelativePath);
                if (!File.Exists(path))
                {
                    missing++;
                    continue;
                }

                if (Same(File.ReadAllText(path, Encoding.UTF8), file.Content)) identical++;
            }

            var current = identical == files.Length;
            var status = missing == files.Length ? "installed"
                : current ? "up-to-date"
                : force ? "updated"
                : "exists";

            return (status, installed, current, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不动就当作"已存在且不认识"：宁可跳过，也不要在不确定的情况下覆盖
            return ("exists", false, false, ex.Message);
        }
    }

    /// <summary>换行差异不算差异：git autocrlf 之后仍应判为 up-to-date。</summary>
    private static bool Same(string left, string right) =>
        left.Replace("\r\n", "\n") == right.Replace("\r\n", "\n");

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
        if (dryRun) yield return "dry-run：以下技能路径没有写入。";

        foreach (var skill in skills)
        {
            var where = $"{skill.agent} → {skill.directory}";
            yield return skill.status switch
            {
                "installed" => dryRun ? $"将安装：{where}" : $"技能已安装：{where}",
                "updated" => dryRun ? $"将覆盖：{where}" : $"技能已更新：{where}",
                "up-to-date" => $"已是最新：{where}",
                _ => dryRun
                    ? $"已存在且内容不同，将跳过：{where}（要覆盖需 --force）"
                    : $"已存在且内容不同，已跳过：{where}（确认要覆盖时重跑并加 --force）",
            };
        }

        if (!command.AddPath) yield break;

        yield return pathStatus switch
        {
            "added" => dryRun
                ? "将把本程序所在目录加入用户 PATH；真正执行后请新开一个终端。"
                : "已把本程序所在目录加入用户 PATH，请新开一个终端让新终端看到它。",
            "already" => "用户 PATH 里已经有本程序所在目录，未重复添加。",
            _ => "未修改用户 PATH。",
        };
    }

    /// <summary>
    /// 读 HKCU\Environment 的 Path 并算出合并结果。
    /// 用 <c>DoNotExpandEnvironmentNames</c> 读，<c>%USERPROFILE%</c> 这类变量才能原样保留。
    /// </summary>
    private static (string newValue, bool changed) MergeUserPath(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: false)
            ?? throw new IOException(@"读不到用户环境变量键 HKCU\Environment。");

        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? string.Empty;
        return PathList.Add(current, directory);
    }

    /// <summary>写回 PATH，并保留原有的值类型：REG_EXPAND_SZ 不能被降级成 REG_SZ，否则变量不再展开。</summary>
    private static void WriteUserPath(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserEnvironmentKey, writable: true)
            ?? throw new IOException(@"写不了用户环境变量键 HKCU\Environment。");

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
            ?? throw new IOException("安装包缺少技能资源：" + name);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
