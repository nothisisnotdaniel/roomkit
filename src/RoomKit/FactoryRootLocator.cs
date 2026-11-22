namespace RoomKit;

/// <summary>Finds the factory root: the configured <c>FactoryRoot</c>, or the parent of the folder holding
/// app's <see cref="RoomKitOptions.SolutionFileName"/>, found by walking up from a start folder.</summary>
internal static class FactoryRootLocator
{
    public static string SolutionFileName => RoomContext.Options.SolutionFileName;

    public const string NotFoundText = "Can't find the factory root. Start the server with --FactoryRoot <path>.";

    public static string? Resolve(string? configured, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string full = Path.GetFullPath(configured);
            return Directory.Exists(full) ? full : null;
        }

        return FindDefault(startDirectory);
    }

    public static string? FindDefault(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.Parent?.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
