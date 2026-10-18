using System.Diagnostics;
using System.Globalization;

namespace InOutButton.Tests;

// spec test 12 at the job itself (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 9)
public class ChildJobTests
{
    // the parent sleeps 2 s first so TryAssign lands before the grandchild exists,
    // then prints the grandchild's pid and exits at once
    private const string ParentScript =
        "Start-Sleep 2; Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru | Select -Expand Id; exit";

    [Fact]
    public async Task ClosingTheJobKillsAGrandchildWhoseParentExited()
    {
        using var job = ChildJob.TryCreate();
        Assert.NotNull(job);

        using var parent = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-NoProfile", "-Command", ParentScript },
            },
        };

        int? grandchild = null;
        try
        {
            parent.Start();
            Assert.True(job.TryAssign(parent), $"AssignProcessToJobObject failed with win32 error {job.LastError}");

            var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            grandchild = int.Parse(line!.Trim(), CultureInfo.InvariantCulture);
            Assert.True(parent.WaitForExit(10_000));
            // the case Kill(entireProcessTree) misses: parent gone, grandchild running
            Assert.True(IsAlive(grandchild.Value, "powershell"));

            job.Dispose();

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (IsAlive(grandchild.Value, "powershell") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(IsAlive(grandchild.Value, "powershell"));
        }
        finally
        {
            Kill(parent);
            if (grandchild is { } pid && IsAlive(pid, "powershell"))
            {
                using var orphan = Process.GetProcessById(pid);
                Kill(orphan);
            }
        }
    }

    [Fact]
    public void AssigningAProcessNeverStartedIsFalse()
    {
        using var job = ChildJob.TryCreate();
        Assert.NotNull(job);
        using var process = new Process();

        Assert.False(job.TryAssign(process));
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // never started or already gone
        }
    }

    private static bool IsAlive(int pid, string name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // a live process under another name is a reused pid
            return !process.HasExited && process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
