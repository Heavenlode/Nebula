using System;
using Godot;

namespace Nebula.Utility.Tools
{
	public partial class ServerClientConnector : Node
	{
		public override void _Ready()
		{
			Debugger.Instance.Log("ServerClientConnector _Ready");
			if (Env.Instance.HasServerFeatures)
			{
				prepareServer();
			}
			else
			{
				prepareClient();
			}
		}

		private void prepareServer()
		{
			NetRunner.Instance.StartServer();
			if (Env.Instance.InitialWorldScene != null)
			{
				Debugger.Instance.Log("Loading initial world scene: " + Env.Instance.InitialWorldScene);
				Debugger.Instance.Log("No existing World data found. Create fresh World instance.");
				var InitialWorldScene = GD.Load<PackedScene>(Env.Instance.InitialWorldScene);
				NetRunner.Instance.CreateWorld(Env.Instance.InitialWorldId, InitialWorldScene);
				Debugger.Instance.Log("Server ready");
			}
			else
			{
				throw new Exception("No initial world scene specified. Provide either a worldId or initialWorldScene in the start args.");
			}
		}

		private void prepareClient()
		{
			Debugger.Instance.Log("ServerClientConnector prepareClient");
			NetRunner.Instance.StartClient();
		}
	}
}
