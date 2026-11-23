namespace RoomKit;

/// <summary>Finds the factory root: the configured <c>FactoryRoot</c>, or the parent of the folder holding
/// app's <see cref="RoomKitOptions.SolutionFileName"/>, found by walking up from a start folder.</summary>
internal static class FactoryRootLocator
{
    public static string SolutionFileName => RoomContext.Options.SolutionFileName;

    public const string NotFoundText = "Can't find the factory root. Start the server with --FactoryRoot <path>.";

    // Decode the scheduler's argument before normal configuration binding so the command line keeps
    // its usual precedence over any FactoryRoot in the environment or appsettings.
    public static string[]? DecodeScheduledArguments(string[] args)
    {
        int option = Array.IndexOf(args, "--FactoryRootBase64");
        if (option < 0)
        {
            return args;
        }

        try
        {
            if (option + 1 == args.Length)
            {
                return null;
            }

            string root = new System.Text.UTF8Encoding(false, true).GetString(Convert.FromBase64String(args[option + 1]));
            if (!Path.IsPathFullyQualified(root))
            {
                return null;
            }

            string[] decoded = (string[])args.Clone();
            decoded[option] = "--FactoryRoot";
            decoded[option + 1] = root;
            return decoded;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return null;
        }
    }

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
