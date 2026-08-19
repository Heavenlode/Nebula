using Nebula.Utility.Tools;

namespace Nebula.Authentication {
  public class DefaultAuthenticator : IAuthenticator {
    public void ClientAuthenticateWithServer() {
      // Client doesn't have to do anything for the default authenticator.
      return;
    }

    public void ServerAuthenticateClient(NetPeer peer) {
      // FirstLiveWorld rather than Worlds.Values.First(): the registry holds a world from the
      // moment its creation starts, so it can contain one that is still generating -- and, while
      // the very first world is being built, can be empty, which threw "Sequence contains no
      // elements" straight out of the ENet pump.
      //
      // NetRunner holds peers that arrive before any world is ready and re-authenticates them once
      // one goes Live, so reaching here with no live world means something admitted this peer out
      // of order rather than that the server is merely still starting up.
      var world = NetRunner.Instance.FirstLiveWorld();
      if (world == null) {
        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
          $"DefaultAuthenticator: no live world to admit peer {peer.ID} into.");
        return;
      }

      NetRunner.Instance.PeerJoinWorld(peer, world.WorldId, NetRunner.Instance.Peers.Count.ToString());
    }
  }
}
