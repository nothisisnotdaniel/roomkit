using System.Net;

namespace RoomKit;

/// <summary>The one address the server listens on: loopback only, the app's
/// <see cref="RoomKitOptions.DefaultPort"/>.</summary>
internal static class ServerEndpoint
{
    public static int Port => RoomContext.Options.DefaultPort;

    public static IPEndPoint Address => new(IPAddress.Loopback, Port);
}
