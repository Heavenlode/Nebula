namespace NebulaTests.Integration.PlayerSpawn;

using Nebula;
using Nebula.Utility.Tools;

public partial class Player : NetNode3D
{
    [NetProperty]
    public int Score { get; set; } = 0;

    public override void _WorldReady()
    {
        base._WorldReady();
        Debugger.Instance.Log("Player _WorldReady");
        (Network.NetParent.Node as Scene).PlayerNode = this;
    }

    public override void _NetworkProcess(int tick)
    {
        base._NetworkProcess(tick);
        if (!NetRunner.Instance.IsServer) return;

        var netInput = Network.GetNetworkInput(0, "").AsString();
        if (netInput == "add_score")
        {
            Score++;
            Network.CurrentWorld.Debug?.Send("ScoreInput", Score.ToString());
        }
        else if (netInput == "subtract_score")
        {
            Score--;
            Network.CurrentWorld.Debug?.Send("ScoreInput", Score.ToString());
        }
    }
}
