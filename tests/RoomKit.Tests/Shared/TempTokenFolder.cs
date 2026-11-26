using System.Text;

namespace RoomKit.Tests;

/// <summary>A temporary folder for a token file, outside any factory root, deleted on dispose. Tests never use the
/// real token file.</summary>
internal sealed class TempTokenFolder : IDisposable
{
    public TempTokenFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "controlroom-token-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A token file path in a not-yet-created sub folder, so the folder mode can be checked.</summary>
    public string TokenFile => System.IO.Path.Combine(Path, "ControlRoom", AccessToken.FileName);

    public static void CreatePrivateDirectory(string path)
    {
        new PrivateFolder(path, UnixOwner.CurrentUser()).EnsureFolder();
    }

    /// <summary>Seed the exact bytes a reader should see, including intentionally malformed content.</summary>
    public static void WritePrivateText(string path, string content)
    {
        FileInfo file = new(path);
        PrivateFolder folder = new(file.DirectoryName!, UnixOwner.CurrentUser());
        folder.Write(file.Name, Encoding.UTF8.GetBytes(content));
    }

    public void Dispose()
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows() && new FileInfo(entry).LinkTarget is null)
            {
                File.SetUnixFileMode(entry, Directory.Exists(entry)
                    ? AccessToken.FolderMode
                    : AccessToken.FileMode);
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}
