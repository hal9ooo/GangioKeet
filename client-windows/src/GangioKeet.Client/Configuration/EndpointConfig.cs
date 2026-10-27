using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

/// <summary>
/// Un endpoint STT configurato dall'utente. <see cref="Key"/> identifica la
/// destinazione di rete (schema, host, porta) e resta stabile quando cambia
/// solo il nome.
/// </summary>
public sealed class EndpointConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "Server";

    /// <summary><c>primary</c> o <c>secondary</c>: etichetta e spareggio.</summary>
    [YamlMember(Alias = "role")]
    public string Role { get; set; } = "primary";

    [YamlMember(Alias = "host")]
    public string Host { get; set; } = "127.0.0.1";

    [YamlMember(Alias = "scheme")]
    public string Scheme { get; set; } = "http";

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 8000;

    [YamlIgnore]
    public bool IsPrimary => string.Equals(Role, "primary", StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public string HostForUri => EndpointHost.ForUri(Host);

    [YamlIgnore]
    public string Key => $"{Scheme}://{HostForUri}:{Port}";
}
