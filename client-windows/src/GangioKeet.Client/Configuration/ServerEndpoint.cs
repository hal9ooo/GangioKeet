namespace GangioKeet.Client.Configuration;

/// <summary>
/// Costruisce gli endpoint HTTP e WebSocket dalla configurazione del server.
/// Lo schema <c>https</c> usa anche <c>wss</c> per il WebSocket.
/// </summary>
public static class ServerEndpoint
{
    public static Uri HttpBase(ServerConfig server) =>
        new($"{server.Scheme}://{server.Host}:{server.Port}");

    public static Uri WebSocket(ServerConfig server) =>
        new($"{(server.Scheme == "https" ? "wss" : "ws")}://{server.Host}:{server.Port}/ws/dictate");

    public static Uri HttpBase(EndpointConfig endpoint) =>
        new($"{endpoint.Scheme}://{endpoint.HostForUri}:{endpoint.Port}");

    public static Uri WebSocket(EndpointConfig endpoint) =>
        new($"{(endpoint.Scheme == "https" ? "wss" : "ws")}://{endpoint.HostForUri}:{endpoint.Port}/ws/dictate");
}
