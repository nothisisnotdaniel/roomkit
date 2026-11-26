namespace RoomKit.Tests;

/// <summary>Source scans that guard the constant-time token compare and ProjectGit's fixed, read-only git calls.
/// Exact-text checks: a harmless refactor of those lines must update the test.</summary>
public sealed class SourceScanTests
{
    [Fact]
    public void Matches_UsesFixedTimeEqualsOnBytes()
    {
        string source = Read("AccessToken.cs");

        AssertHas(source, "AccessToken.cs", "CryptographicOperations.FixedTimeEquals(left, right)");
        AssertHas(source, "AccessToken.cs", "Encoding.UTF8.GetBytes(presented)");
    }

    [Fact]
    public void ProjectGit_StartsOnlyGitWithFixedCalls()
    {
        string source = Read("ProjectGit.cs");

        AssertHas(source, "ProjectGit.cs", "ProcessStartInfo start = new(\"git\")");
        AssertHas(source, "ProjectGit.cs", "[\"rev-list\", \"--count\", \"refs/heads/main..refs/heads/\" + Branch]");
        AssertHas(source, "ProjectGit.cs", "[\"rev-list\", \"--count\", range]");
    }

    [Fact]
    public void ProjectGit_ReleaseCallsAreFixed()
    {
        string source = Read("ProjectGit.cs");

        AssertHas(source, "ProjectGit.cs",
            "[\"for-each-ref\", \"--format=%(refname:strip=2)%09%(creatordate:unix)\", \"refs/tags/\"]");
        AssertHas(source, "ProjectGit.cs",
            "[\"log\", \"--first-parent\", \"--format=%h%x1f%s\", \"--max-count=\" + MaxLog, range]");
        AssertHas(source, "ProjectGit.cs", "public const int MaxLog = 1000;");
        AssertHas(source, "ProjectGit.cs",
            "return IsReleaseTag(tag) ? \"refs/tags/\" + tag + \"..refs/heads/\" + Branch : null;");
    }

    [Fact]
    public void ProjectGit_UsesNoNetworkOrShell()
    {
        string source = Read("ProjectGit.cs");

        foreach (string forbidden in new[] { "\"gh\"", "\"curl\"", "HttpClient", "UseShellExecute = true" })
        {
            Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                $"ProjectGit.cs must not contain {forbidden}.");
        }
    }

    private static string Read(string name)
    {
        string path = Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit", name);
        Assert.True(File.Exists(path), $"{path} is missing.");
        return File.ReadAllText(path);
    }

    private static void AssertHas(string source, string file, string expected) =>
        Assert.True(source.Contains(expected, StringComparison.Ordinal), $"{file} no longer contains: {expected}");
}
