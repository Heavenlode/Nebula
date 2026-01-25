namespace NebulaTests.Integration.PlayerSpawn;

using System.Runtime.InteropServices;
using Nebula;
using Nebula.Utility.Tools;

/// <summary>
/// Input commands for testing.
/// </summary>
public enum TestCommand : byte
{
    None = 0,
    AddScore = 1,
    SubtractScore = 2,
    ClearInput = 3,
    // Generic test commands for HandlesInput test
    Foo = 10,
    Bar = 11,
}

/// <summary>
/// Simple input struct for testing.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TestInput
{
    public TestCommand Command;
    public byte Channel; // Used for test verification
}

public partial class Player : NetNode3D
{
    [NetProperty]
    public int Score { get; set; } = 0;
    
    // Track last input to only send debug event once per input change
    private TestCommand _lastSentCommand = TestCommand.None;
    private byte _lastSentChannel = 0;

    public Player()
    {
        Network.InitializeInput<TestInput>();
    }

    public override void _WorldReady()
    {
        base._WorldReady();
        Debugger.Instance.Log("Player _WorldReady");
        (Network.NetParent.NetNode as Scene).PlayerNode = this;
    }

    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);
        if (!NetRunner.Instance.IsServer) return;

        ref readonly var input = ref Network.GetInput<TestInput>();
        
        // Send debug event only once per unique input (not for None)
        bool isNewInput = input.Command != _lastSentCommand || input.Channel != _lastSentChannel;
        if (isNewInput && input.Command != TestCommand.None)
        {
            var commandStr = input.Command switch
            {
                TestCommand.AddScore => "add_score",
                TestCommand.SubtractScore => "subtract_score",
                TestCommand.ClearInput => "clear_input",
                TestCommand.Foo => "foo",
                TestCommand.Bar => "bar",
                _ => "unknown"
            };
            var debugMsg = $"{input.Channel}:{commandStr}";
            Network.CurrentWorld.Debug?.Send("Input", debugMsg);
            _lastSentCommand = input.Command;
            _lastSentChannel = input.Channel;
        }
        
        // Process the command
        switch (input.Command)
        {
            case TestCommand.AddScore:
                Score++;
                Network.CurrentWorld.Debug?.Send("ScoreInput", Score.ToString());
                break;
            case TestCommand.SubtractScore:
                Score--;
                Network.CurrentWorld.Debug?.Send("ScoreInput", Score.ToString());
                break;
            case TestCommand.ClearInput:
                // No-op - just clears the input state
                // Debug event is already sent by the tracking logic above
                break;
            case TestCommand.None:
            default:
                break;
        }
    }
}
