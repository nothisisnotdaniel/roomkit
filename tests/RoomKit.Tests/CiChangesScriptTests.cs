using System.ComponentModel;
using System.Diagnostics;

namespace RoomKit.Tests;

/// <summary>Runs the Python suite of the CI change check (<c>.github/scripts/test_ci_changes.py</c>, which tests
/// <c>ci_changes.py</c> with <c>git_identity.py</c>) under <c>python3 -m unittest</c>. The three files are copied into
/// this test's own temp folder (deleted on dispose), so no <c>__pycache__</c> reaches the repo, and the test waits for
/// python3 to exit within <see cref="PythonExitTimeout"/>.</summary>
public sealed class CiChangesScriptTests : IDisposable
{
    private static readonly TimeSpan PythonExitTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] Scripts = ["ci_changes.py", "test_ci_changes.py", "git_identity.py"];

    private readonly string folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roomkit-ci-changes-" + Guid.NewGuid().ToString("N"))).FullName;

    [PythonFact]
    public void PythonSuite_Passes()
    {
        string scripts = Path.Combine(RepoCopy.RepoFolder(), ".github", "scripts");
        foreach (string script in Scripts)
        {
            File.Copy(Path.Combine(scripts, script), Path.Combine(folder, script));
        }

        ProcessStartInfo start = new("python3")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-m", "unittest", "discover", "-s", folder, "-p", "test_ci_changes.py" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception error)
        {
            Assert.Fail("CiChangesScriptTests needs python3 on PATH: " + error.Message);
            throw;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(PythonExitTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the ci_changes.py suite did not exit within " + PythonExitTimeout);
            }

            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                "the ci_changes.py suite failed (exit " + process.ExitCode + "):\n" + output.Result + "\n" + errors.Result);
        }
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }
}
