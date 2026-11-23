using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace RoomKit.Tests;

/// <summary>RoomKit as a library: no app values in its sources, a small friend list, every public type documented,
/// and a package that holds the DLL, the web files and the scripts.</summary>
public sealed class RoomKitLibraryTests
{
    private static readonly string[] AppValues =
        ["ControlRoom", "controlroom", "8100", "state.json", "nothisisnotdaniel", "Control Room"];

    [Fact]
    public void Sources_HoldNoControlRoomValue()
    {
        string kit = Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit");
        string[] files = [.. new[] { "*.cs", "*.js", "*.css", "*.html" }
            .SelectMany(pattern => Directory.GetFiles(kit, pattern, SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(kit, file))];

        Assert.Contains(files, file => file.EndsWith("RoomKitApp.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("sw.js", StringComparison.Ordinal));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string value in AppValues)
            {
                Assert.False(
                    text.Contains(value, StringComparison.Ordinal),
                    Path.GetRelativePath(kit, file) + " contains " + value);
            }
        }
    }

    [Fact]
    public void Project_ReferencesNoAppAndFriendsOnlyTheTwoTestAssemblies()
    {
        string project = File.ReadAllText(
            Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit", "RoomKit.csproj"));
        XDocument xml = XDocument.Parse(project);

        Assert.Empty(xml.Descendants("ProjectReference"));
        Assert.Empty(xml.Descendants("PackageReference"));
        Assert.Equal(
            ["ControlRoom.Server.Tests", "RoomKit.Tests"],
            xml.Descendants("InternalsVisibleTo").Select(item => (string)item.Attribute("Include")!).Order(StringComparer.Ordinal));

        string[] friends = [.. typeof(RoomKitApp).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName).Order(StringComparer.Ordinal)];
        Assert.Equal(["ControlRoom.Server.Tests", "RoomKit.Tests"], friends);
        Assert.DoesNotContain(
            typeof(RoomKitApp).Assembly.GetReferencedAssemblies(),
            name => name.Name!.StartsWith("ControlRoom", StringComparison.Ordinal));
    }

    [Fact]
    public void Docs_HaveTheSectionsAndNameEveryPublicType()
    {
        string docs = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), "docs", "roomkit.md"));
        string[] sections =
        [
            "What is in it", "Options", "Job format", "Web assets and tabs", "Scripts", "Feed and versioning",
            "Plugging in an app",
        ];
        int last = -1;
        foreach (string section in sections)
        {
            int index = docs.IndexOf("\n## " + section + "\n", StringComparison.Ordinal);
            Assert.True(index > last, "missing or out of order: ## " + section);
            last = index;
        }

        Type[] types = typeof(RoomKitApp).Assembly.GetExportedTypes();
        Assert.Contains(typeof(RoomKitOptions), types);
        Assert.Contains(typeof(IJobFormat), types);
        foreach (Type type in types)
        {
            Assert.True(docs.Contains("`" + type.Name + "`", StringComparison.Ordinal), "docs/roomkit.md misses " + type.Name);
        }

        Assert.Contains("~/Library/Application Support/RoomKit/feed", docs, StringComparison.Ordinal);
        Assert.Contains(
            "gh release download vX.Y.Z --repo nothisisnotdaniel/controlroom --pattern 'RoomKit.*.nupkg'",
            docs,
            StringComparison.Ordinal);
        Assert.Contains("read:packages", docs, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_GivesTheVersionedPackageWithTheDllWebFilesAndScripts()
    {
        using RepoCopy copy = new();

        copy.Dotnet("pack", "src/RoomKit", "-c", "Release", "-p:Version=1.2.3", "-o", copy.Output);

        string package = Path.Combine(copy.Output, "RoomKit.1.2.3.nupkg");
        Assert.True(File.Exists(package), "RoomKit.1.2.3.nupkg is missing");
        using ZipArchive zip = ZipFile.OpenRead(package);
        string[] entries = [.. zip.Entries.Select(entry => entry.FullName)];

        ZipArchiveEntry nuspecEntry = Assert.Single(zip.Entries, entry => entry.FullName == "RoomKit.nuspec");
        using Stream nuspecStream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(nuspecStream);
        XElement metadata = nuspec.Root!.Elements().Single(element => element.Name.LocalName == "metadata");
        Assert.Equal("RoomKit", Value(metadata, "id"));
        Assert.Equal("1.2.3", Value(metadata, "version"));
        Assert.Equal("docs/roomkit.md", Value(metadata, "readme"));
        Assert.DoesNotContain(metadata.Descendants(), element => element.Name.LocalName == "dependency");

        Assert.Contains("lib/net10.0/RoomKit.dll", entries);
        Assert.Contains("docs/roomkit.md", entries);
        Assert.Contains("scripts/launch-agent.sh", entries);
        Assert.Contains("scripts/updater.sh", entries);
        string scripts = Path.Combine(copy.Path, "packed-scripts");
        Directory.CreateDirectory(scripts);
        foreach (string name in new[] { "launch-agent.ps1", "updater.ps1" })
        {
            ZipArchiveEntry entry = Assert.Single(zip.Entries, item => item.FullName == "scripts/" + name);
            string path = Path.Combine(scripts, name);
            entry.ExtractToFile(path);
            Assert.Equal(File.ReadAllBytes(Path.Combine(copy.Path, "src", "RoomKit", "scripts", name)), File.ReadAllBytes(path));
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsReleaseFixture.Run(copy.Path, scripts, "sample", new FakeTime());
        }

        string kit = Path.Combine(copy.Path, "src", "RoomKit", "wwwroot");
        foreach (string file in Directory.GetFiles(kit, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(kit, file).Replace('\\', '/');
            Assert.Contains("staticwebassets/" + relative, entries);
        }

        // The package's static web assets are served at the app's root path.
        ZipArchiveEntry props = Assert.Single(
            zip.Entries, entry => entry.FullName == "build/Microsoft.AspNetCore.StaticWebAssets.props");
        using StreamReader reader = new(props.Open());
        string text = reader.ReadToEnd();
        Assert.Contains("<BasePath>/</BasePath>", text, StringComparison.Ordinal);
        Assert.Contains("<RelativePath>sw.js</RelativePath>", text, StringComparison.Ordinal);
    }

    private static string Value(XElement metadata, string name)
    {
        return metadata.Elements().Single(element => element.Name.LocalName == name).Value;
    }

    private static bool IsBuildOutput(string root, string file)
    {
        string first = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }
}
