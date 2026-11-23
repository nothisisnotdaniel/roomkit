namespace RoomKit;

/// <summary>The <c>token</c> command: prints the access token (creating it when missing), or with <c>--new</c>
/// replaces it and prints the new one. Never starts the server.</summary>
internal static class TokenCommand
{
    public const string Name = "token";

    public const string NewOption = "--new";

    public static string UsageText =>
        "Usage: " + RoomContext.RunCommand + " token [--new] [--TokenFile <path>]";

    public static bool IsCommand(string[] args)
    {
        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    /// <summary>Runs the command with the arguments after <c>token</c>; the token goes only to
    /// <paramref name="output"/>, fixed texts to <paramref name="error"/>.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        return Run(args, output, error, AccessToken.SystemRandom);
    }

    public static int Run(string[] args, TextWriter output, TextWriter error, RandomFill random)
    {
        bool replace = false;
        string? configured = null;
        for (int index = 1; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, NewOption, StringComparison.Ordinal))
            {
                replace = true;
            }
            else if (string.Equals(arg, "--" + AccessToken.ConfigKey, StringComparison.OrdinalIgnoreCase)
                     && index + 1 < args.Length)
            {
                configured = args[index + 1];
                index++;
            }
            else
            {
                error.WriteLine(UsageText);
                return 1;
            }
        }

        configured ??= Environment.GetEnvironmentVariable(AccessToken.ConfigKey);
        string path = AccessToken.ResolvePath(configured);
        string? token;
        if (replace)
        {
            token = AccessToken.Replace(path, random);
        }
        else
        {
            TokenFileRead read = AccessToken.Read(path);
            if (read.State == TokenFileState.Invalid)
            {
                error.WriteLine(AccessToken.InvalidText);
                return 1;
            }

            token = read.State == TokenFileState.Valid ? read.Token : AccessToken.Replace(path, random);
        }

        if (token is null)
        {
            error.WriteLine(AccessToken.WriteFailedText);
            return 1;
        }

        output.Write(token + "\n");
        return 0;
    }
}
