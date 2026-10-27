using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Configuration;

/// <summary>Fornisce l'endpoint attivo al controller, una volta per dettatura.</summary>
public interface IEndpointResolver
{
    EndpointConfig Active { get; }
}
