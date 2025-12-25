#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaTests.Integration;

/// <summary>
/// Wrapper around a Godot process for integration testing.
/// Handles spawning, stdout capture, stdin commands, and cleanup.
/// </summary>
public sealed class GodotProcess : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly StringBuilder _allOutput = new();
    private readonly object _outputLock = new();
    private bool _disposed;

    public string AllOutput
    {
        get
        {
            lock (_outputLock)
            {
                return _allOutput.ToString();
            }
        }
    }

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    private GodotProcess(Process process)
    {
        _process = process;

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            
            lock (_outputLock)
            {
                _allOutput.AppendLine(e.Data);
            }
            _outputLines.Enqueue(e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            
            lock (_outputLock)
            {
                _allOutput.AppendLine($"[STDERR] {e.Data}");
            }
        };

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Starts a new Godot process with the given arguments.
    /// </summary>
    /// <param name="args">Command line arguments for Godot</param>
    /// <param name="workingDirectory">Working directory (defaults to current directory)</param>
    /// <returns>A new GodotProcess instance</returns>
    public static GodotProcess Start(string[] args, string? workingDirectory = null)
    {
        var godotBin = Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(godotBin))
        {
            throw new InvalidOperationException(
                "GODOT_BIN environment variable is not set. " +
                "Set it to the path of your Godot executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = godotBin,
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        var process = new Process { StartInfo = startInfo };
        
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Godot process: {godotBin}");
        }

        return new GodotProcess(process);
    }

    /// <summary>
    /// Waits for a specific string to appear in the stdout output.
    /// </summary>
    /// <param name="pattern">The string to wait for</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <returns>The line containing the pattern</returns>
    public async Task<string> WaitForOutput(string pattern, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var cts = new CancellationTokenSource(timeout.Value);

        // First check existing output
        lock (_outputLock)
        {
            var lines = _allOutput.ToString().Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains(pattern))
                {
                    return line;
                }
            }
        }

        // Poll for new output
        while (!cts.Token.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Godot process exited (code {_process.ExitCode}) while waiting for '{pattern}'. " +
                    $"Output:\n{AllOutput}");
            }

            while (_outputLines.TryDequeue(out var line))
            {
                if (line.Contains(pattern))
                {
                    return line;
                }
            }

            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for '{pattern}' after {timeout.Value.TotalSeconds}s. " +
            $"Output so far:\n{AllOutput}");
    }

    /// <summary>
    /// Sends a command to the process via stdin.
    /// </summary>
    /// <param name="command">The command to send</param>
    public void SendCommand(string command)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Cannot send command - process has exited (code {_process.ExitCode})");
        }

        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary>
    /// Waits for the process to exit.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    public async Task WaitForExit(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        
        var exited = await Task.Run(() => _process.WaitForExit((int)timeout.Value.TotalMilliseconds));
        
        if (!exited)
        {
            throw new TimeoutException($"Process did not exit within {timeout.Value.TotalSeconds}s");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort cleanup
        }
        finally
        {
            _process.Dispose();
        }
    }
}


