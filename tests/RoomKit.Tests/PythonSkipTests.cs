namespace RoomKit.Tests;

public sealed class PythonSkipTests
{
    [Fact]
    public void Windows_GivesAWindowsReason()
    {
        string? reason = PythonSkip.SkipReason(true, _ => true, "/usr/bin");

        Assert.NotNull(reason);
        Assert.Contains("Windows", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/bin:/bin")]
    [InlineData("")]
    [InlineData(null)]
    public void NoPython3_GivesAReasonNamingPython3(string? path)
    {
        string? reason = PythonSkip.SkipReason(false, _ => false, path);

        Assert.NotNull(reason);
        Assert.Contains("python3", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/bin")]
    [InlineData("/opt/homebrew/bin")]
    [InlineData("/usr/local/bin")]
    public void Python3InAnyPathFolder_GivesNull(string present)
    {
        string path = string.Join(Path.PathSeparator, "/usr/bin", "/opt/homebrew/bin", "/usr/local/bin");

        Assert.Null(PythonSkip.SkipReason(false, file => file == Path.Combine(present, "python3"), path));
    }

    [Fact]
    public void Python3OnlyOutsidePath_GivesAReason()
    {
        Assert.NotNull(PythonSkip.SkipReason(false, file => file == Path.Combine("/elsewhere", "python3"), "/usr/bin"));
    }

    [Fact]
    public void Attribute_SetsSkipToTheReasonForThisMachine()
    {
        Assert.Equal(PythonSkip.SkipReason(), new PythonFactAttribute().Skip);
    }
}
