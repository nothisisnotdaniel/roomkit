using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RoomKit.Tests;

/// <summary>A temporary factory root with sample projects, deleted on dispose. Tests never use the real
/// factory root.</summary>
internal sealed class TempFactoryRoot : IDisposable
{
    public const string NormalBacklog =
        "# Normal backlog\n"
        + "\n"
        + "Some prose with - [ ] inside.\n"
        + "- [ ] First open: do `this` → 125-x\n"
        + "- [?] Proposal: wait for me\n"
        + "  - [ ] Indented: ignored\n"
        + "- [h] Hand task: for you\n"
        + "- [x] Done one: finished\n"
        + "- [ ] Second open without rest\n"
        + "- [X] Done two: a: b\n"
        + "- [y] Other mark: ignored\n"
        + "-  [ ] Bad spacing: ignored\n"
        + "- [ ]No space: ignored\n"
        + "* [ ] Star: ignored\n";

    private readonly List<string> lockedFiles = [];
    private readonly List<FileStream> exclusiveHandles = [];

    public TempFactoryRoot()
    {
        string name = "controlroom-tests-" + Guid.NewGuid().ToString("N");
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a project folder with a <c>CLAUDE.md</c> and, when given, a <c>backlog.md</c>.</summary>
    public string AddProject(string name, string? backlog)
    {
        string folder = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "CLAUDE.md"), "# " + name + "\n");
        if (backlog is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(folder, "backlog.md"), backlog);
        }

        return folder;
    }

    /// <summary>Writes a story file into the project folder's <c>backlog</c> folder (created when missing) and
    /// returns its path.</summary>
    public static string AddStory(string projectFolder, string fileName, string content)
    {
        string folder = System.IO.Path.Combine(projectFolder, "backlog");
        Directory.CreateDirectory(folder);
        string file = System.IO.Path.Combine(folder, fileName);
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>Makes the folder a real git repo on <paramref name="branch"/> with a local identity (no signing,
    /// hooks from <c>.git/hooks</c>) and commits everything in it.</summary>
    public static void InitGit(string folder, string branch)
    {
        Git(folder, "init", "-q", "-b", branch);
        // The same settings five `git config` calls would write, appended in one go: each git process costs time.
        File.AppendAllText(
            System.IO.Path.Combine(folder, ".git", "config"),
            "[user]\n\tname = Test User\n\temail = test@example.invalid\n"
            + "[commit]\n\tgpgsign = false\n"
            + "[core]\n\tautocrlf = false\n\thooksPath = .git/hooks\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "init");
    }

    /// <summary>Seeds a local bare origin under the fixture's <c>.remotes</c> folder.</summary>
    public string AddBareRemote(string folder)
    {
        string remote = System.IO.Path.Combine(Path, ".remotes", new DirectoryInfo(folder).Name + ".git");
        Directory.CreateDirectory(remote);
        Git(remote, "init", "-q", "--bare");
        // Seed the initial ref locally, so only the API operation under test needs a Git transport.
        string bundle = remote + ".bundle";
        string branch = Git(folder, "symbolic-ref", "HEAD");
        Git(folder, "bundle", "create", bundle, branch);
        Git(remote, "bundle", "unbundle", bundle);
        Git(remote, "update-ref", branch, Git(folder, "rev-parse", "HEAD"));
        File.Delete(bundle);
        Git(folder, "remote", "add", "origin", remote);
        return remote;
    }

    /// <summary>Runs git in the folder with a fixed argument list and returns its trimmed output; fails the test
    /// when git fails.</summary>
    public static string Git(string folder, params string[] arguments)
    {
        return GitWithInput(folder, null, arguments);
    }

    /// <summary>Like <see cref="Git"/>, and writes <paramref name="input"/> (when given) to git's standard input,
    /// for example a <c>git fast-import</c> stream that makes many commits in one process.</summary>
    public static string GitWithInput(string folder, string? input, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = input is not null,
            StandardInputEncoding = input is null ? null : new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> outputRead = process.StandardOutput.ReadToEndAsync();
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        string output = outputRead.Result;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git " + string.Join(" ", arguments) + ": " + error.Result);
        return output.Trim();
    }

    /// <summary>The jobs folder <c>factory/jobs</c> under the root.</summary>
    public string JobsPath => System.IO.Path.Combine(Path, "factory", "jobs");

    /// <summary>Creates the job folder <c>factory/jobs/&lt;id&gt;</c> and, when given, its <c>state.json</c>, and
    /// returns the folder's path.</summary>
    public string AddJob(string id, string? stateJson)
    {
        string folder = System.IO.Path.Combine(JobsPath, id);
        Directory.CreateDirectory(folder);
        if (stateJson is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(folder, "state.json"), stateJson);
        }

        return folder;
    }

    /// <summary>Writes a record (a path relative to the job folder, folders created when missing) and returns its
    /// path.</summary>
    public string AddRecord(string id, string relativePath, string text)
    {
        string file = System.IO.Path.Combine(JobsPath, id, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    /// <summary>The full sample set from the spec's test plan.</summary>
    public static TempFactoryRoot WithSamples()
    {
        TempFactoryRoot root = new();
        root.AddProject("Normal", NormalBacklog);
        Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "NoClaude"));
        File.WriteAllText(System.IO.Path.Combine(root.Path, "NoClaude", "backlog.md"), "- [ ] Hidden: no\n");
        root.AddProject("factory", "- [ ] Factory: no\n");
        root.AddProject(".hidden", "- [ ] Hidden: no\n");
        root.AddProject("Missing", null);
        root.AddProject("Oversized", new string('a', (int)BacklogReader.MaxBytes) + "\n- [ ] Big: no\n");
        root.AddProject("emptyOne", "# Nothing here\n\nJust prose.\n");
        File.WriteAllText(System.IO.Path.Combine(root.Path, "CLAUDE.md"), "# root file\n");

        string outside = System.IO.Path.Combine(root.Path, ".outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(System.IO.Path.Combine(outside, "CLAUDE.md"), "# outside\n");
        File.WriteAllText(System.IO.Path.Combine(outside, "backlog.md"), "- [ ] Secret: outside\n");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(root.Path, "LinkedFolder"), outside);

        string linkedBacklog = root.AddProject("LinkedBacklog", null);
        File.CreateSymbolicLink(
            System.IO.Path.Combine(linkedBacklog, "backlog.md"), System.IO.Path.Combine(outside, "backlog.md"));
        return root;
    }

    /// <summary>Makes a file unreadable until fixture teardown.</summary>
    public void Lock(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            exclusiveHandles.Add(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None));
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
        }

        lockedFiles.Add(file);
    }

    /// <summary>Creates a named pipe (FIFO) at the path with <c>mkfifo</c> (Unix only; tests only).</summary>
    public static void MakeFifo(string path)
    {
        using Process process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Every file and folder under the root with its size, time and content hash.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        List<string> entries = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(Path, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = 0,
                     IgnoreInaccessible = true,
                 }))
        {
            FileInfo info = new(entry);
            string hash = "";
            if (info.Exists && info.LinkTarget is null && !lockedFiles.Contains(entry))
            {
                hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
            }

            entries.Add(entry + "|" + info.LastWriteTimeUtc.Ticks + "|" + hash);
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    /// <summary>On Windows, clears the read-only flag git sets on object files so the folder can be deleted.</summary>
    public static void ClearReadOnly(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Git marks object files read-only. Leave links alone while preparing the fixture for deletion.
        EnumerationOptions files = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", files))
        {
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }
        }
    }

    public void Dispose()
    {
        foreach (FileStream handle in exclusiveHandles)
        {
            handle.Dispose();
        }

        if (OperatingSystem.IsWindows())
        {
            ClearReadOnly(Path);
        }
        else
        {
            foreach (string file in lockedFiles)
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}
