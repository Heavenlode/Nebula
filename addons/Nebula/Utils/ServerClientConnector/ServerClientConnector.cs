using System;
using Godot;
using System.Threading.Tasks;

namespace Nebula.Utility.Tools
{
	public partial class ServerClientConnector : Node
	{
		public override void _Ready()
		{
			GD.Print("ServerClientConnector _Ready");
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

		private async void prepareClient()
		{
			Debugger.Instance.Log("ServerClientConnector prepareClient");
			// Slight delay to allow the server to fully setup before the clients connect
			await Task.Delay(300);
			NetRunner.Instance.StartClient();
		}
	}
}
