namespace RoomKit.Tests;

public sealed class ShellScriptSkipTests
{
    [Fact]
    public void Windows_GivesAWindowsReason()
    {
        string? reason = ShellScriptSkip.SkipReason(true, _ => true);

        Assert.NotNull(reason);
        Assert.Contains("Windows", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoJq_GivesAReasonNamingJq()
    {
        string? reason = ShellScriptSkip.SkipReason(false, _ => false);

        Assert.NotNull(reason);
        Assert.Contains("jq", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/bin/jq")]
    [InlineData("/bin/jq")]
    [InlineData("/opt/homebrew/bin/jq")]
    [InlineData("/usr/local/bin/jq")]
    public void JqInAnyPathFolder_GivesNull(string present)
    {
        Assert.Null(ShellScriptSkip.SkipReason(false, path => path == present));
    }

    [Fact]
    public void LoginCheckTests_HaveNoWindowsEarlyReturn()
    {
        string text = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), "tests", "RoomKit.Tests", "PushLoginCheckScriptTests.cs"));

        Assert.DoesNotContain("IsWindows()", text, StringComparison.Ordinal);
    }
}
