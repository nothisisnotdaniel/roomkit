namespace RoomKit;

/// <summary>A project found in the factory root: its folder name and full path.</summary>
public sealed record ProjectFolder(string Name, string Path);

/// <summary>Finds the projects of the factory root: top-level folders with a <c>CLAUDE.md</c>, apart from
/// <c>factory</c>, dot-folders and symlinked folders, sorted by name.</summary>
public static class ProjectFinder
{
    public static IReadOnlyList<ProjectFolder> Find(string factoryRoot)
    {
        List<ProjectFolder> projects = [];
        IEnumerable<DirectoryInfo> folders;
        try
        {
            folders = new DirectoryInfo(factoryRoot).EnumerateDirectories().ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return projects;
        }

        foreach (DirectoryInfo folder in folders)
        {
            if (IsProject(folder))
            {
                projects.Add(new ProjectFolder(folder.Name, folder.FullName));
            }
        }

        projects.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return projects;
    }

    private static bool IsProject(DirectoryInfo folder)
    {
        if (folder.Name.StartsWith('.') || string.Equals(folder.Name, "factory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (folder.LinkTarget is not null)
            {
                return false;
            }

            return File.Exists(Path.Combine(folder.FullName, "CLAUDE.md"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
