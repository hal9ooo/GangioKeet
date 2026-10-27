using System.Net.Http;

namespace GangioKeet.Client.Tests.Fakes;

/// <summary>Handler HTTP che delega a una funzione e registra le richieste.</summary>
public sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}
