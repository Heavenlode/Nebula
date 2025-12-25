namespace NebulaTests.Integration.PlayerSpawn;

using Nebula;
using Nebula.Utility.Tools;

public partial class Player : NetNode3D
{
    public override void _WorldReady()
    {
        base._WorldReady();
        Debugger.Instance.Log("Player _WorldReady");
    }
}
