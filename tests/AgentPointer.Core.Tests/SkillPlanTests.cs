using AgentPointer.Core.Cli;
using Xunit;

namespace AgentPointer.Core.Tests;

public class SkillPlanTests
{
    private const string Skill = "SKILL.md";
    private const string OpenAi = "agents/openai.yaml";

    private static SkillPlan.FileInput Missing(string path, string desired) => new(path, desired, false, null);

    private static SkillPlan.FileInput Existing(string path, string current, string desired) =>
        new(path, desired, true, current);

    [Fact]
    public void WritesEveryFileWhenNothingExists()
    {
        var plan = SkillPlan.Plan([Missing(Skill, "a"), Missing(OpenAi, "b")], force: false);

        Assert.Equal("installed", plan.Status);
        Assert.True(plan.Installed);
        Assert.All(plan.Files, file => Assert.Equal(SkillFileAction.Write, file.Action));
    }

    [Fact]
    public void WritesMissingFilesInAPartialTree()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "a", "a"), Missing(OpenAi, "b")], force: false);

        Assert.Equal("updated", plan.Status);
        Assert.True(plan.Installed);
        Assert.Equal(SkillFileAction.SkipSame, plan.ActionFor(Skill));
        Assert.Equal(SkillFileAction.Write, plan.ActionFor(OpenAi));
    }

    [Fact]
    public void KeepsUpToDateTreesUntouched()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "a", "a"), Existing(OpenAi, "b", "b")], force: false);

        Assert.Equal("up-to-date", plan.Status);
        Assert.True(plan.Installed);
        Assert.All(plan.Files, file => Assert.Equal(SkillFileAction.SkipSame, file.Action));
    }

    [Fact]
    public void SkipsUserEditedFilesWithoutForce()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "mine", "ours"), Missing(OpenAi, "b")], force: false);

        Assert.Equal("exists", plan.Status);
        Assert.False(plan.Installed);
        Assert.Equal(SkillFileAction.SkipDiffers, plan.ActionFor(Skill));
        Assert.Equal(SkillFileAction.Write, plan.ActionFor(OpenAi));
    }

    [Fact]
    public void ForceOverwritesUserEditedFiles()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "mine", "ours")], force: true);

        Assert.Equal("updated", plan.Status);
        Assert.True(plan.Installed);
        Assert.Equal(SkillFileAction.Write, plan.ActionFor(Skill));
    }

    [Fact]
    public void TreatsAMovedExecutableAsAnUpdateWithoutForce()
    {
        const string current = "run `& 'C:\\old\\agentpointer.exe' doctor --json`";
        const string desired = "run `& 'C:\\new\\agentpointer.exe' doctor --json`";

        var plan = SkillPlan.Plan([Existing(Skill, current, desired)], force: false);

        Assert.Equal("updated", plan.Status);
        Assert.True(plan.Installed);
        Assert.Equal(SkillFileAction.Write, plan.ActionFor(Skill));
    }

    [Fact]
    public void TreatsRealEditsAsDifferent()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "Click Save", "Click Cancel")], force: false);

        Assert.Equal("exists", plan.Status);
        Assert.Equal(SkillFileAction.SkipDiffers, plan.ActionFor(Skill));
    }

    [Fact]
    public void IgnoresLineEndingDifferences()
    {
        var plan = SkillPlan.Plan([Existing(Skill, "a\r\nb", "a\nb")], force: false);

        Assert.Equal("up-to-date", plan.Status);
    }

    [Fact]
    public void ReportsReadErrors()
    {
        var plan = SkillPlan.Plan([new SkillPlan.FileInput(Skill, "a", true, null, "denied")], force: true);

        Assert.Equal("error", plan.Status);
        Assert.False(plan.Installed);
    }
}
