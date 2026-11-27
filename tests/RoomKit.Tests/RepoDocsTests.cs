namespace RoomKit.Tests;

/// <summary><c>CLAUDE.md</c> and the "Feed and versioning" section of <c>docs/roomkit.md</c>, read as text, and no
/// <c>nuget.config</c> in the repo.</summary>
public sealed class RepoDocsTests
{
    private static readonly string[] TemplateTexts =
    [
        "none yet",
        "one or two sentences on what this project does",
        "The first factory job picks the stack",
        "where the source code, the tests and the entry points live",
        "naming, formatting, libraries to prefer or avoid",
    ];

    [Fact]
    public void ClaudeMd_IsFilledInAndNamesTheTestCommand()
    {
        string text = Read("CLAUDE.md");

        foreach (string template in TemplateTexts)
        {
            Assert.DoesNotContain(template, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string heading in new[] { "What it is", "Stack", "Test command", "Layout", "Style", "Git" })
        {
            Assert.Contains("- **" + heading + ":**", text, StringComparison.Ordinal);
        }

        Assert.Contains("**Test command:** `dotnet test`", text, StringComparison.Ordinal);
        Assert.Contains("`release vX.Y.Z`", text, StringComparison.Ordinal);
        Assert.Contains("RoomKit.slnx", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Docs_FeedSection_NamesGitHubPackagesAndTheConsumerSetup()
    {
        string docs = Read(Path.Combine("docs", "roomkit.md"));
        string section = Section(docs, "Feed and versioning");

        Assert.Contains("https://nuget.pkg.github.com/nothisisnotdaniel/index.json", section, StringComparison.Ordinal);
        Assert.Contains("read:packages", section, StringComparison.Ordinal);
        Assert.Contains("NuGetPackageSourceCredentials_github", section, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget add source", section, StringComparison.Ordinal);
        Assert.Contains("packages: read", section, StringComparison.Ordinal);
        Assert.Contains("`release vX.Y.Z`", section, StringComparison.Ordinal);
        Assert.DoesNotContain("--repo nothisisnotdaniel/controlroom", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("RoomKit/feed", docs, StringComparison.Ordinal);
    }

    [Fact]
    public void Docs_AppBadgeSection_NamesTheBadgeSourceAndItsSetter()
    {
        string docs = Read(Path.Combine("docs", "roomkit.md"));
        int start = docs.IndexOf("\n### App badge\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/roomkit.md has no App badge section");
        int end = docs.IndexOf("\n#", start + 1, StringComparison.Ordinal);
        string section = end < 0 ? docs[start..] : docs[start..end];

        Assert.Contains("`IPushBadgeSource`", section, StringComparison.Ordinal);
        Assert.Contains("SetPushBadge", section, StringComparison.Ordinal);
        Assert.Contains("Push badge failed; sent without a badge", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Repo_HoldsNoNuGetConfig()
    {
        string repo = RepoCopy.RepoFolder();
        string[] configs = [.. Directory.GetFiles(repo, "*", SearchOption.AllDirectories)
            .Where(file => !IsSkipped(repo, file))
            .Where(file => string.Equals(Path.GetFileName(file), "nuget.config", StringComparison.OrdinalIgnoreCase))];

        Assert.Empty(configs);
    }

    private static string Read(string relative)
    {
        return File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), relative)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The text after <c>## <paramref name="name"/></c> up to the next <c>## </c> heading.</summary>
    private static string Section(string docs, string name)
    {
        string header = "\n## " + name + "\n";
        int start = docs.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing ## " + name);
        start += header.Length;
        int end = docs.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? docs[start..] : docs[start..end];
    }

    private static bool IsSkipped(string root, string file)
    {
        string[] parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part is ".git" or "bin" or "obj");
    }
}
