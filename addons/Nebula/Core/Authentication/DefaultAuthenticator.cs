using System;
using System.Linq;

namespace Nebula.Authentication {
  public class DefaultAuthenticator : IAuthenticator {
    public void ClientAuthenticateWithServer() {
      // Client doesn't have to do anything for the default authenticator.
      return;
    }

    public void ServerAuthenticateClient(NetPeer peer) {
      NetRunner.Instance.PeerJoinWorld(peer, NetRunner.Instance.Worlds.Values.First().WorldId, NetRunner.Instance.Peers.Count.ToString());
    }
  }
}