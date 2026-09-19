#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Nebula.Testing;

/// <summary>
/// Runs a headless Godot scene from a test host and returns what it printed.
///
/// <para>Every caller here spawns Godot with both standard streams redirected, and a redirected
/// stream that nobody reads is a deadlock, not an inconvenience: the pipe holds 64 KiB on Linux,
/// after which the child blocks forever trying to write. A parent sitting in ReadToEnd() on the
/// other stream then never returns either, so nothing times out and nothing is printed - the run
/// simply stops. That is what wedged CI for six hours once the unit suite's stderr (Godot prints a
/// full stack trace for every deliberate PushError) crossed the buffer. Both streams are drained
/// concurrently here, and a wedged child is killed and reported instead of waited on forever.</para>
/// </summary>
internal static class HeadlessGodot
{
    /// <summary>Result of one headless run. Both streams are always fully captured.</summary>
    internal sealed class Result
    {
        public required int ExitCode { get; init; }
        public required string StandardOutput { get; init; }
        public required string StandardError { get; init; }
    }

    /// <summary>
    /// How long to keep draining after the child is gone. The readers see EOF the moment it
    /// exits, so this only ever covers the handoff; it is not a second execution budget.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="arguments"/> under the Godot binary named by the GODOT environment
    /// variable and returns its output once it exits.
    /// </summary>
    /// <exception cref="InvalidOperationException">GODOT is unset.</exception>
    /// <exception cref="TimeoutException">
    /// The child outlived <paramref name="timeout"/>. It is killed, along with anything it spawned,
    /// and whatever both streams produced is quoted in the message - a wedged suite reports where
    /// it stopped rather than dying silently at the CI job limit.
    /// </exception>
    public static Result Run(string arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        var godotBin = Environment.GetEnvironmentVariable("GODOT");
        if (string.IsNullOrEmpty(godotBin))
        {
            throw new InvalidOperationException(
                "GODOT environment variable is not set. " +
                "Set it to the path of your Godot executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = godotBin,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Both pipes drain from their own thread, started before anything blocks. Reading one
        // to the end first is the deadlock described above.
        var stdout = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderr = Task.Run(() => process.StandardError.ReadToEnd());

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            KillTree(process);
            Task.WaitAll(new Task[] { stdout, stderr }, DrainGrace);
            throw new TimeoutException(
                $"Godot did not exit within {timeout.TotalSeconds:0}s and was killed.\n" +
                $"Command: {godotBin} {arguments}\n" +
                $"Output so far:\n{Captured(stdout)}\n" +
                $"Stderr so far:\n{Captured(stderr)}");
        }

        // The child is gone, so both readers are at EOF; this is the handoff, not a wait.
        Task.WaitAll(new Task[] { stdout, stderr }, DrainGrace);

        return new Result
        {
            ExitCode = process.ExitCode,
            StandardOutput = Captured(stdout),
            StandardError = Captured(stderr),
        };
    }

    /// <summary>Whatever a reader has finished with, never a throw: this runs on failure paths.</summary>
    private static string Captured(Task<string> reader)
    {
        try
        {
            return reader.IsCompletedSuccessfully ? reader.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Godot spawns children of its own; killing only the parent leaves them holding the pipe,
    /// which keeps the readers open and defeats the point of the timeout.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone between the timeout and here.
        }
    }

    /// <summary>
    /// The test project directory - the one holding project.godot, found by walking up from the
    /// test assembly. Shared so the fixture and the unit-test host cannot disagree about it.
    /// </summary>
    public static string? FindTestProjectPath()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "project.godot")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback - tests launched from the repository root rather than the build output.
        var testPath = Path.Combine(Environment.CurrentDirectory, "test");
        if (File.Exists(Path.Combine(testPath, "project.godot")))
        {
            return testPath;
        }

        return null;
    }
}
