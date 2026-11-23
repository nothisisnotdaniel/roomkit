using System.Security.Cryptography;
using System.Text;

namespace RoomKit;

/// <summary>Fills a buffer with random bytes (the default is <see cref="RandomNumberGenerator.Fill"/>).</summary>
internal delegate void RandomFill(Span<byte> buffer);

/// <summary>What a read of the token file found.</summary>
internal enum TokenFileState
{
    /// <summary>No file (and no symlink or folder) at the path.</summary>
    Missing,

    /// <summary>A private regular file holding a valid token.</summary>
    Valid,

    /// <summary>A symlink, a non-regular file, an unreadable file, a file readable by group or others, or content
    /// that is not a valid token.</summary>
    Invalid,
}

/// <summary>The result of reading the token file: the state and, when valid, the token.</summary>
internal sealed class TokenFileRead
{
    public TokenFileRead(TokenFileState state, string? token)
    {
        State = state;
        Token = token;
    }

    public TokenFileState State { get; }

    public string? Token { get; }
}

/// <summary>The one access token: 32 random bytes as base64url without padding (43 chars), kept in a private file
/// outside the factory root (folder <c>0700</c>, file <c>0600</c>, replaced atomically). The token is never
/// written anywhere else.</summary>
internal static class AccessToken
{
    public const int ByteLength = 32;

    public const int TextLength = 43;

    public const string FileName = "access-token";

    public const string ConfigKey = "TokenFile";

    /// <summary>Largest token file read (a token plus a line ending fits many times over).</summary>
    public const long MaxBytes = 256;

    public static string CreatedText(string? configured) =>
        "Created an access token. Show it with " + RoomContext.RunCommand + " token" + TokenFileArgument(configured);

    public static string InvalidText(string? configured) =>
        FolderRecovery(configured) ?? "Replace the invalid access token file with "
        + RoomContext.RunCommand + " token --new" + TokenFileArgument(configured);

    public static string WriteFailedText(string? configured) =>
        FolderRecovery(configured) ?? "Can't write the access token file.";

    private static string TokenFileArgument(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "";
        }

        string path = ResolvePath(configured);
        string literal = OperatingSystem.IsWindows() ? RoomContext.PowerShellLiteral(path)
            : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return " --TokenFile " + literal;
    }

    private static string? FolderRecovery(string? configured)
    {
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo folder = new FileInfo(ResolvePath(configured)).Directory!;
            if (folder.Exists || folder.LinkTarget is not null)
            {
                try
                {
                    using FileStream handle = WindowsPrivateFiles.OpenDirectory(folder);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return $"Use a real directory for the access token folder '{folder.FullName}'. "
                        + "In Properties > Security > Advanced, set your account as owner, disable inheritance "
                        + "and grant access only to your account. Then restart the server.";
                }
            }
        }

        return null;
    }

    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>The configured path, or <c>&lt;LocalApplicationData&gt;/&lt;data folder&gt;/access-token</c> (on macOS
    /// <c>~/Library/Application Support/&lt;data folder&gt;/access-token</c>), the folder being the app's
    /// <see cref="RoomKitOptions.DataFolderName"/>.</summary>
    public static string ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        string data = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(data, RoomContext.Options.DataFolderName, FileName);
    }

    /// <summary>A new token from the random source: base64url of <see cref="ByteLength"/> bytes, no padding.</summary>
    public static string Generate(RandomFill random)
    {
        byte[] bytes = new byte[ByteLength];
        random(bytes);
        string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(bytes);
        return token;
    }

    /// <summary>True when the text has the token's exact form: 43 base64url characters.</summary>
    public static bool IsValid(string? token)
    {
        if (token is null || token.Length != TextLength)
        {
            return false;
        }

        foreach (char c in token)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads the token file without following a symlink; anything but a private regular file with a valid
    /// token (optionally followed by one line ending) is <see cref="TokenFileState.Invalid"/>.</summary>
    public static TokenFileRead Read(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (file.LinkTarget is not null || Directory.Exists(path))
            {
                return Invalid();
            }

            if (!file.Exists)
            {
                if (OperatingSystem.IsWindows() && file.Directory is { } folder
                    && (folder.Exists || folder.LinkTarget is not null))
                {
                    WindowsPrivateFiles.Check(folder);
                }

                return new TokenFileRead(TokenFileState.Missing, null);
            }

            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & GroupOrOther) != 0)
            {
                return Invalid();
            }

            byte[]? bytes = OperatingSystem.IsWindows()
                ? WindowsPrivateFiles.Read(file, MaxBytes)
                : SafeFileReader.ReadBytes(path, MaxBytes);
            if (bytes is null)
            {
                return Invalid();
            }

            string text = SafeFileReader.Decode(bytes);
            string token = text.EndsWith('\n') ? text[..^1] : text;
            return IsValid(token) ? new TokenFileRead(TokenFileState.Valid, token) : Invalid();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid();
        }
    }

    /// <summary>Writes a new token: the folder is created with mode <c>0700</c> when missing, the token goes to a
    /// new <c>0600</c> temp file in the same folder, which is then renamed over the path (a symlink at the path is
    /// replaced itself, never written through). Returns the new token, or <c>null</c> when writing failed.</summary>
    public static string? Replace(string path, RandomFill random)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full)!;
        string temp = Path.Combine(folder, "." + FileName + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        string token = Generate(random);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo directory = new(folder);
                if (!directory.Exists && directory.LinkTarget is null)
                {
                    WindowsPrivateFiles.CreateDirectory(directory);
                }

                using FileStream parent = WindowsPrivateFiles.OpenDirectory(directory);
                WindowsPrivateFiles.Replace(parent, Path.GetFileName(full), Encoding.ASCII.GetBytes(token + "\n"));
                return token;
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder, FolderMode);
                File.SetUnixFileMode(folder, FolderMode);
            }

            using (FileStream stream = CreatePrivateFile(temp))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(token + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temp, FileMode);

            File.Move(temp, full, overwrite: true);
            return token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                TryDelete(temp);
            }
            return null;
        }
    }

    /// <summary>Compares a presented token with the stored one in constant time (on the UTF-8 bytes); a different
    /// length is refused.</summary>
    public static bool Matches(string expected, string? presented)
    {
        if (presented is null)
        {
            return false;
        }

        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>The default random source.</summary>
    public static void SystemRandom(Span<byte> buffer)
    {
        RandomNumberGenerator.Fill(buffer);
    }

    private static TokenFileRead Invalid()
    {
        return new TokenFileRead(TokenFileState.Invalid, null);
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        return OperatingSystem.IsWindows() ? WindowsPrivateFiles.CreateFile(path) : new FileStream(path,
            new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = FileMode,
            });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the temp file holds no token unless the write got that far.
        }
    }
}
