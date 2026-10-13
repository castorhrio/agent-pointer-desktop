using System.Text;
using System.Text.Json;

namespace AgentPointer.Cli;

internal static class SkillInstaller
{
    public static int Install(bool json, bool dryRun)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) throw new IOException("无法确定用户目录。");
        var directories = new[]
        {
            Path.Combine(profile, ".claude", "skills", "agentpointer"),
            Path.Combine(profile, ".agents", "skills", "agentpointer"),
        };
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定程序路径。");
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var invocation = "& " + Quote(executable);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            invocation += " " + Quote(typeof(SkillInstaller).Assembly.Location);
        var files = new[]
        {
            new { RelativePath = "SKILL.md", Content = ReadResource("AgentPointer.Skill.md").Replace("__AGENTPOINTER_COMMAND__", invocation) },
            new { RelativePath = Path.Combine("agents", "openai.yaml"), Content = ReadResource("AgentPointer.Skill.openai.yaml") },
        };

        if (!dryRun)
        {
            foreach (var directory in directories)
            foreach (var file in files)
            {
                var destination = Path.Combine(directory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, file.Content, new UTF8Encoding(false));
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        var skills = directories.Select((directory, index) =>
        {
            var installed = File.Exists(Path.Combine(directory, "SKILL.md"));
            var current = false;
            string? error = null;
            try
            {
                current = files.All(file =>
                {
                    var path = Path.Combine(directory, file.RelativePath);
                    return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n")
                        == file.Content.Replace("\r\n", "\n");
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            return new { agent = index == 0 ? "Claude Code" : "Codex", directory, installed, current, error };
        }).ToArray();

        Console.WriteLine(json
            ? JsonSerializer.Serialize(new { ok = true, dryRun, installed = !dryRun, directories, skills })
            : (dryRun ? "将安装技能到：" : "技能已安装到：") + string.Join("、", directories));
        return Core.ExitCode.Ok;
    }

    private static string ReadResource(string name)
    {
        using var resource = typeof(SkillInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new IOException("安装包缺少技能资源：" + name);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
