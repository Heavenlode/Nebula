namespace Nebula.Tools;

using Godot;
using System.Text;

/// <summary>
/// Root of the dummy scene the Nebula plugin plays via PlayCustomScene to force
/// the editor's debug server to start listening, so self-spawned play instances
/// can attach as debugger sessions. Does nothing but minimize its window and
/// idle — it must stay alive as long as the attached sessions are wanted,
/// because the editor closes the debug server when this session ends.
///
/// <para>Godot launches this scene once per configured run instance, and the
/// project's Run Instances settings are the user's, not Nebula's — the plugin
/// used to rewrite them to force a single headless instance, which silently
/// wiped whatever the user had configured, on every editor start. The duplicates
/// sort themselves out here instead: the process that wins
/// <see cref="SINGLE_INSTANCE_PORT"/> is the holder, and the rest quit once they
/// have confirmed who is holding it. Safe because
/// <c>EditorRunBar::stop_child_process</c> only ends the play session once EVERY
/// child process is gone, so the extras exiting does not close the debug
/// server.</para>
/// </summary>
public partial class NebulaDebugSession : Node
{
    /// <summary>
    /// Loopback port used purely as a single-instance lock — binding it is the
    /// atomic "I am the holder" claim, and nothing is served over it beyond
    /// <see cref="GreetingBytes"/>. Deliberately below both Linux's (32768+) and
    /// macOS's (49152+) ephemeral ranges, so the OS never hands this port to one
    /// of the Nebula debug channels that <c>Main.ReserveLoopbackPort</c> asks
    /// for.
    /// </summary>
    private const int SINGLE_INSTANCE_PORT = 31415;

    /// <summary>
    /// Sent by the holder to anything that connects. A duplicate quits only once
    /// it has read this back: without the check, an unrelated process happening
    /// to hold the port would make EVERY instance quit, leaving nothing holding
    /// the debug server open and no visible reason why.
    /// </summary>
    private static readonly byte[] GreetingBytes = Encoding.ASCII.GetBytes("NEBULA_DEBUG_SESSION\n");

    /// <summary>
    /// How long a duplicate waits for the holder's greeting before giving up and
    /// staying alive. The bias is deliberate: a redundant minimized instance
    /// costs some memory, whereas quitting when nobody else is holding the port
    /// breaks the debugger for the entire play session.
    /// </summary>
    private const double GREETING_TIMEOUT_SECONDS = 2.0;

    /// <summary>Held by the winning process for as long as it lives.</summary>
    private TcpServer lockServer;

    /// <summary>Non-null only while a duplicate is confirming who holds the port.</summary>
    private StreamPeerTcp holderProbe;
    private double sinceProbeStarted;

    public override void _Ready()
    {
        var window = GetWindow();
        if (window is not null)
        {
            window.Title = "Nebula Debug Session";
            window.Mode = Window.ModeEnum.Minimized;
        }

        SetProcess(true);

        var server = new TcpServer();
        if (server.Listen(SINGLE_INSTANCE_PORT, "127.0.0.1") == Error.Ok)
        {
            lockServer = server;
            GD.Print("NEBULA_DEBUG_SESSION: holding the editor debug server open.");
            return;
        }

        holderProbe = new StreamPeerTcp();
        if (holderProbe.ConnectToHost("127.0.0.1", SINGLE_INSTANCE_PORT) == Error.Ok)
            return;

        holderProbe = null;
        GD.Print("NEBULA_DEBUG_SESSION: single-instance port unreachable; staying alive.");
    }

    public override void _Process(double delta)
    {
        if (lockServer is not null)
        {
            GreetDuplicates();
            return;
        }
        if (holderProbe is not null)
            PollHolderProbe(delta);
    }

    public override void _ExitTree()
    {
        lockServer?.Stop();
        lockServer = null;
    }

    /// <summary>
    /// Answers every connection with the greeting and drops it — the connection
    /// itself is the whole protocol.
    /// </summary>
    private void GreetDuplicates()
    {
        while (lockServer.IsConnectionAvailable())
            lockServer.TakeConnection()?.PutData(GreetingBytes);
    }

    private void PollHolderProbe(double delta)
    {
        sinceProbeStarted += delta;
        holderProbe.Poll();
        var status = holderProbe.GetStatus();

        if (status == StreamPeerTcp.Status.Connected && holderProbe.GetAvailableBytes() >= GreetingBytes.Length)
        {
            var result = holderProbe.GetData(GreetingBytes.Length);
            bool greeted = (Error)result[0].AsInt32() == Error.Ok && IsGreeting(result[1].AsByteArray());
            AbandonProbe();

            if (greeted)
            {
                GD.Print("NEBULA_DEBUG_SESSION: another instance already holds the debug server; exiting.");
                GetTree().Quit();
                return;
            }
            GD.Print("NEBULA_DEBUG_SESSION: single-instance port answered by something else; staying alive.");
            return;
        }

        if (status == StreamPeerTcp.Status.Error || sinceProbeStarted >= GREETING_TIMEOUT_SECONDS)
        {
            AbandonProbe();
            GD.Print("NEBULA_DEBUG_SESSION: no Nebula holder answered; staying alive.");
        }
    }

    private void AbandonProbe()
    {
        holderProbe.DisconnectFromHost();
        holderProbe = null;
    }

    private static bool IsGreeting(byte[] received)
    {
        if (received.Length != GreetingBytes.Length)
            return false;
        for (int i = 0; i < received.Length; i++)
        {
            if (received[i] != GreetingBytes[i])
                return false;
        }
        return true;
    }
}
