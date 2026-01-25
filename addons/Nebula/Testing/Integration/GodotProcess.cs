#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula.Testing.Integration;

/// <summary>
/// Represents a debug event received from the Godot process.
/// </summary>
public struct DebugEvent
{
    public string Category { get; set; }
    public string Message { get; set; }
}

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

    // Debug connection fields (TCP)
    private TcpClient? _debugClient;
    private NetworkStream? _debugStream;
    private readonly ConcurrentQueue<DebugEvent> _debugEvents = new();
    private CancellationTokenSource? _debugListenerCts;
    private Task? _debugListenerTask;
    private bool _debugConnected;

    /// <summary>
    /// Optional label to identify this process in logs (e.g., "server", "client1").
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The port used for debug TCP connection. Set by StartServer/StartClient.
    /// </summary>
    public int DebugPort { get; set; }

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
        timeout ??= TimeSpan.FromSeconds(3);
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
    /// Requests a scene tree dump from the Godot process and returns it.
    /// Requires the process to have a StdinCommandHandler that handles the dump_tree command.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for the dump</param>
    /// <returns>The scene tree dump as a string, or null if the process has exited</returns>
    public async Task<string?> RequestSceneTreeDump(TimeSpan? timeout = null)
    {
        if (_disposed)
        {
            return "[Process already disposed]";
        }

        try
        {
            if (_process.HasExited)
            {
                return $"[Process has exited with code {_process.ExitCode}]\nFinal output:\n{AllOutput}";
            }
        }
        catch (InvalidOperationException)
        {
            return "[Process already disposed]";
        }

        timeout ??= TimeSpan.FromSeconds(5);

        try
        {
            SendCommand("dump_tree");

            // Wait for the dump markers
            await WaitForOutput("[SCENE_TREE_DUMP_START]", timeout);
            await WaitForOutput("[SCENE_TREE_DUMP_END]", timeout);

            // Extract the dump from all output
            var dump = ExtractSceneTreeDump();
            return $"{dump}\n\nOutput so far:\n{AllOutput}";
        }
        catch (Exception ex)
        {
            return $"[Failed to get scene tree dump: {ex.Message}]\nOutput so far:\n{AllOutput}";
        }
    }

    private string ExtractSceneTreeDump()
    {
        var output = AllOutput;
        var startMarker = "[SCENE_TREE_DUMP_START]";
        var endMarker = "[SCENE_TREE_DUMP_END]";

        var startIdx = output.LastIndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = output.LastIndexOf(endMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx)
        {
            return "[Could not parse scene tree dump]";
        }

        return output.Substring(startIdx, endIdx - startIdx + endMarker.Length);
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

    /// <summary>
    /// Connects to the Godot process's debug TCP server.
    /// </summary>
    /// <param name="port">The debug port to connect to. If 0, uses DebugPort property.</param>
    public async Task ConnectDebug(int port = 0)
    {
        if (port == 0) port = DebugPort;
        if (port == 0) throw new InvalidOperationException("Debug port not set");

        _debugClient = new TcpClient();

        // Retry connection for a few seconds (server might not be ready yet)
        var connectionTimeout = TimeSpan.FromSeconds(5);
        var connectionCts = new CancellationTokenSource(connectionTimeout);

        while (!_debugConnected && !connectionCts.Token.IsCancellationRequested)
        {
            try
            {
                await _debugClient.ConnectAsync("127.0.0.1", port);
                _debugConnected = true;
                _debugStream = _debugClient.GetStream();
            }
            catch (SocketException)
            {
                // Server not ready yet, retry
                await Task.Delay(100);
            }
        }

        if (!_debugConnected)
        {
            throw new TimeoutException($"Failed to connect to debug server on port {port}");
        }

        // Start listener task for incoming debug events
        _debugListenerCts = new CancellationTokenSource();
        _debugListenerTask = Task.Run(() =>
        {
            var buffer = new byte[4096];
            var messageBuffer = new MemoryStream();

            // Set read timeout to allow checking cancellation periodically
            _debugStream!.ReadTimeout = 100; // 100ms timeout

            try
            {
                while (!_debugListenerCts!.Token.IsCancellationRequested && _debugConnected)
                {
                    try
                    {
                        // Use synchronous read with timeout instead of unreliable Available check
                        var bytesRead = _debugStream!.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                        {
                            _debugConnected = false;
                            break;
                        }

                        // Process received data - may contain multiple messages
                        messageBuffer.Write(buffer, 0, bytesRead);
                        ProcessMessages(messageBuffer);
                    }
                    catch (IOException)
                    {
                        // Read timeout - this is expected, just continue the loop
                        // to check cancellation and try again
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _debugConnected = false; }
        }, _debugListenerCts.Token);
    }

    private void ProcessMessages(MemoryStream messageBuffer)
    {
        var data = messageBuffer.ToArray();
        var offset = 0;

        while (offset < data.Length)
        {
            // Check if we have enough data for header (4 bytes length + 1 byte type)
            if (offset + 5 > data.Length) break;

            // Read message length (first 4 bytes)
            int msgLen = BitConverter.ToInt32(data, offset);

            // Sanity check - message length should be reasonable
            if (msgLen < 0 || msgLen > 1000000) break;

            // Check if we have the full message
            if (offset + 4 + msgLen > data.Length) break;

            // Extract message data (skip length prefix)
            var msgData = new byte[msgLen];
            Array.Copy(data, offset + 4, msgData, 0, msgLen);

            // Parse debug event - Format: [byte type][string category][string message]
            if (msgData.Length > 0 && msgData[0] == 6) // DEBUG_EVENT = 6
            {
                var evt = ParseDebugEvent(msgData);
                if (evt.HasValue)
                {
                    _debugEvents.Enqueue(evt.Value);
                }
            }

            offset += 4 + msgLen;
        }

        // Keep remaining incomplete data in buffer
        if (offset < data.Length)
        {
            var remaining = new byte[data.Length - offset];
            Array.Copy(data, offset, remaining, 0, remaining.Length);
            messageBuffer.SetLength(0);
            messageBuffer.Write(remaining, 0, remaining.Length);
        }
        else
        {
            messageBuffer.SetLength(0);
        }
    }

    private DebugEvent? ParseDebugEvent(byte[] data)
    {
        try
        {
            // Skip the type byte
            int offset = 1;

            // Read category string (length-prefixed with 4-byte int)
            if (offset + 4 > data.Length) return null;
            int categoryLen = BitConverter.ToInt32(data, offset);
            offset += 4;

            if (offset + categoryLen > data.Length) return null;
            string category = Encoding.UTF8.GetString(data, offset, categoryLen);
            offset += categoryLen;

            // Read message string (length-prefixed with 4-byte int)
            if (offset + 4 > data.Length) return null;
            int messageLen = BitConverter.ToInt32(data, offset);
            offset += 4;

            if (offset + messageLen > data.Length) return null;
            string message = Encoding.UTF8.GetString(data, offset, messageLen);

            return new DebugEvent { Category = category, Message = message };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets all currently queued debug events as a formatted string without removing them.
    /// </summary>
    /// <returns>A formatted string of all debug events</returns>
    public string GetDebugBuffer()
    {
        var sb = new StringBuilder();
        var events = _debugEvents.ToArray();

        if (events.Length == 0)
        {
            sb.AppendLine("[No debug events in buffer]");
        }
        else
        {
            foreach (var evt in events)
            {
                sb.AppendLine($"[{evt.Category}] {evt.Message}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Waits for a debug event matching the specified category and message pattern.
    /// </summary>
    /// <param name="category">Event category to match</param>
    /// <param name="messagePattern">Message pattern to match (uses Contains)</param>
    /// <param name="timeout">Maximum time to wait</param>
    public async Task<DebugEvent> WaitForDebugEvent(string category, string messagePattern = "", TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var cts = new CancellationTokenSource(timeout.Value);

        while (!cts.Token.IsCancellationRequested)
        {
            while (_debugEvents.TryDequeue(out var evt))
            {
                if (evt.Category == category && (messagePattern == "" || evt.Message == messagePattern))
                {
                    return evt;
                }
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Godot process exited while waiting for debug event '{category}:{messagePattern}'. " +
                    $"Output:\n{AllOutput}");
            }

            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for debug event '{category}:{messagePattern}' after {timeout.Value.TotalSeconds}s. " +
            $"Output so far:\n{AllOutput}");
    }

    /// <summary>
    /// Ensures no debug event matching the specified category and message pattern occurs within the timeout.
    /// </summary>
    /// <param name="category">Event category to match</param>
    /// <param name="messagePattern">Message pattern to match (uses Contains)</param>
    /// <param name="timeout">Time period during which the event must not occur</param>
    /// <exception cref="InvalidOperationException">Thrown if the event occurs within the timeout</exception>
    public async Task EnsureNoDebugEvent(string category, string messagePattern, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMilliseconds(100);
        var cts = new CancellationTokenSource(timeout.Value);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                while (_debugEvents.TryDequeue(out var evt))
                {
                    if (evt.Category == category && evt.Message.Contains(messagePattern))
                    {
                        throw new InvalidOperationException(
                            $"Unexpected debug event '{category}:{messagePattern}' occurred. " +
                            $"Event message: {evt.Message}\n" +
                            $"Output:\n{AllOutput}");
                    }
                }

                if (_process.HasExited)
                {
                    // Process exited without the event occurring - that's fine
                    return;
                }

                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached without the event occurring - success
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cleanup debug connection
        try
        {
            _debugListenerCts?.Cancel();
            _debugListenerTask?.Wait(1000);

            _debugStream?.Close();
            _debugClient?.Close();
            _debugClient?.Dispose();
        }
        catch
        {
            // Best effort cleanup
        }

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
