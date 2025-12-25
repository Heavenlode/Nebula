using Godot;
using System;
using Nebula;
using Nebula.Utility.Tools;

namespace NebulaTests.Integration.Tests.PlayerSpawn
{
    public partial class Player : NetNode3D
    {
        public override void _WorldReady()
        {
            base._WorldReady();
            Debugger.Instance.Log("Player _WorldReady");
        }
    }

}
