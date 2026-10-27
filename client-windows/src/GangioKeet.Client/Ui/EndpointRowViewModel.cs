using System.ComponentModel;
using System.Runtime.CompilerServices;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Riga modificabile della finestra impostazioni. Non tocca la rete né il file:
/// il code-behind compila <see cref="ToConfig"/> solo al salvataggio o alla
/// verifica ad-hoc. Le stringhe di stato passano da <see cref="EndpointFormatPolicy"/>.
/// </summary>
public sealed class EndpointRowViewModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _host = "";
    private string _port = "8000";
    private string _scheme = "http";
    private string _role = "secondary";

    private string _statusKind = "unknown";
    private string _latencyText = "—";
    private string _readyText = "—";
    private string _errorText = "";
    private string _probeText = "";
    private bool _isActive;
    private bool _isLoaded;
    private bool _adHoc;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Destinazione da cui la riga è stata caricata, per ritrovare le misure dopo un rename.</summary>
    public string? OriginalKey { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string Scheme { get => _scheme; set => Set(ref _scheme, value); }
    public string Role { get => _role; set => Set(ref _role, value); }

    /// <summary>online, offline o unknown: la XAML ne deriva il colore del pallino.</summary>
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string ReadyText { get => _readyText; private set => Set(ref _readyText, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string ProbeText { get => _probeText; private set => Set(ref _probeText, value); }
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    public static EndpointRowViewModel From(EndpointConfig endpoint) => new()
    {
        OriginalKey = endpoint.Key,
        Name = endpoint.Name,
        Host = endpoint.Host,
        Port = endpoint.Port.ToString(),
        Scheme = endpoint.Scheme,
        Role = endpoint.Role,
        _isLoaded = true,
    };

    /// <summary>Compila l'endpoint con i valori correnti; la porta non valida diventa 0 e la validazione la rifiuta.</summary>
    public EndpointConfig ToConfig() => new()
    {
        Name = Name.Trim(),
        Host = Host.Trim(),
        Port = EndpointFormatPolicy.ParsePort(Port) ?? 0,
        Scheme = Scheme,
        Role = Role,
    };

    public bool IsValid(out string error)
    {
        if (!EndpointFormatPolicy.IsValidName(Name))
        {
            error = "nome mancante o troppo lungo";
            return false;
        }

        if (!EndpointFormatPolicy.IsValidHost(Host.Trim()))
        {
            error = "host non valido (niente schema o porta)";
            return false;
        }

        if (EndpointFormatPolicy.ParsePort(Port) is null)
        {
            error = "porta non valida (1-65535)";
            return false;
        }

        if (Scheme is not ("http" or "https"))
        {
            error = "schema non valido";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>Aggiorna lo stato periodico; non tocca i campi in editor.</summary>
    public void ApplyStats(EndpointStats? stats, bool isActive)
    {
        IsActive = isActive;

        // Un esito ad-hoc resta finché la finestra è aperta: il tick periodico
        // non lo sovrascrive.
        if (_adHoc)
        {
            return;
        }

        if (!_isLoaded || stats is null)
        {
            ResetStats();
            return;
        }

        StatusKind = stats.Reachable ? "online" : stats.Measured ? "offline" : "unknown";
        LatencyText = EndpointFormatPolicy.Latency(stats.MedianLatencyMs);
        ReadyText = EndpointFormatPolicy.Ready(stats.Ready, stats.Busy, stats.Draining);
        ErrorText = stats.LastError ?? "";
    }

    /// <summary>Esito della verifica ad-hoc, mostrato solo sulla riga.</summary>
    public void ApplyProbe(ProbeResult result)
    {
        _adHoc = true;
        StatusKind = result.Reachable ? "online" : "offline";
        LatencyText = EndpointFormatPolicy.Latency(result.LatencyMs);
        ReadyText = EndpointFormatPolicy.Ready(result.Ready, result.Busy, result.Draining);
        ErrorText = result.ErrorCode ?? "";
        ProbeText = result.Reachable ? "verificato adesso" : "probe fallito";
    }

    public void ApplyProbeError(string message)
    {
        _adHoc = true;
        StatusKind = "offline";
        LatencyText = "—";
        ReadyText = "—";
        ErrorText = message;
        ProbeText = "probe non eseguito";
    }

    public void MarkLoaded()
    {
        _isLoaded = true;
        _adHoc = false;
        ProbeText = "";
    }

    private void ResetStats()
    {
        StatusKind = "unknown";
        LatencyText = "—";
        ReadyText = "—";
        ErrorText = "";
    }

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? property = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
