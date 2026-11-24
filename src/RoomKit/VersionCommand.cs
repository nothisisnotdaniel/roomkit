namespace RoomKit;

/// <summary>The <c>version</c> command: prints <c>&lt;short name&gt; &lt;version&gt;</c>. Never reads the token file or
/// starts the server.</summary>
internal static class VersionCommand
{
    public const string Name = "version";

    public static string UsageText => "Usage: " + RoomContext.RunCommand + " version";

    public static bool IsCommand(string[] args)
    {
        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 1)
        {
            error.WriteLine(UsageText);
            return 1;
        }

        output.Write(RoomContext.Options.ShortName + " " + AppVersion.Current + "\n");
        return 0;
    }
}
