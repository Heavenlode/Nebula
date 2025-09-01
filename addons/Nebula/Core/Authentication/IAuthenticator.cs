namespace Nebula.Authentication {
  public interface IAuthenticator {
    public void ClientAuthenticateWithServer();
    public void ServerAuthenticateClient(NetPeer peer);
  }
}