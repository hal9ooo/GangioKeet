using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Configurazione degli endpoint con stato live e telemetria in sola lettura.
/// Il code-behind collega solo eventi e Dispatcher: formattazione e validazione
/// stanno in <see cref="EndpointFormatPolicy"/> e <see cref="EndpointRowViewModel"/>.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private EndpointSelector? _selector;
    private EndpointProbe? _probe;
    private readonly TelemetryStore? _telemetry;
    private readonly string _configPath;
    private Action<IReadOnlyList<EndpointConfig>> _onSaved;
    private readonly FileLogger? _logger;
    private readonly Func<LocalSettingsInfo>? _localInfo;
    private readonly Action<string>? _onModeSaved;
    private Action<string>? _onModelPairSaved;
    private RecordingStore? _recordingStore;
    private RecordingStorage? _recordingStorage;
    private bool _recordingRefresh;
    private readonly DispatcherTimer _timer;

    private TimeSpan _probeTimeout = TimeSpan.FromSeconds(2);
    private bool _initializing;

    public SettingsWindow(
        EndpointSelector? selector,
        EndpointProbe? probe,
        TelemetryStore? telemetry,
        string configPath,
        Action<IReadOnlyList<EndpointConfig>> onSaved,
        FileLogger? logger = null,
        Func<LocalSettingsInfo>? localInfo = null,
        Action<string>? onModeSaved = null,
        RecordingStore? recordingStore = null,
        Action<string>? onModelPairSaved = null)
    {
        _selector = selector;
        _probe = probe;
        _telemetry = telemetry;
        _configPath = configPath;
        _onSaved = onSaved;
        _logger = logger;
        _localInfo = localInfo;
        _onModeSaved = onModeSaved;
        _recordingStore = recordingStore;
        _recordingStorage = recordingStore is null ? null : new RecordingStorage(recordingStore);
        _onModelPairSaved = onModelPairSaved;

        InitializeComponent();
        RowsList.ItemsSource = Rows;

        string initialMode = localInfo?.Invoke().Mode ?? (_selector is null ? "local" : "remote");
        ApplyModeView(initialMode);

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher);
        _timer.Start();

        Loaded += (_, _) => ReloadFromDisk();
        Closed += (_, _) => _timer.Stop();
    }

    public ObservableCollection<EndpointRowViewModel> Rows { get; } = [];
    public bool UsesLocalServices => _selector is null;
    public bool IsLocalPanelVisible => LocalPanel.Visibility == Visibility.Visible;
    public bool IsModeSelectorVisible => ModeCombo.Visibility == Visibility.Visible;
    public string LocalState => LocalStateText.Text;
    public string LocalPair => LocalPairText.Text;

    private void LoadFromConfig(ClientConfig config)
    {
        Rows.Clear();
        SetMode(config.Inference.Mode);
        ApplyModeView(config.Inference.Mode);
        UpdateModelStackButtons(config.Inference.ModelSelectionPath);

        if (config.Endpoints is null)
        {
            return;
        }

        foreach (EndpointConfig endpoint in config.Endpoints)
        {
            Rows.Add(EndpointRowViewModel.From(endpoint));
        }

        _probeTimeout = TimeSpan.FromSeconds(config.Server.HealthCheckTimeoutSeconds);
    }

    private void SetMode(string mode)
    {
        _initializing = true;
        foreach (ComboBoxItem item in ModeCombo.Items)
        {
            if (string.Equals(item.Tag as string, mode, StringComparison.Ordinal))
            {
                ModeCombo.SelectedItem = item;
                break;
            }
        }
        _initializing = false;
    }

    private void ReloadFromDisk()
    {
        try
        {
            ClientConfig config = File.Exists(_configPath)
                ? ConfigLoader.Load(_configPath)
                : ConfigLoader.CreateDefaults();
            LoadFromConfig(config);
            RecordingCheck.IsChecked = config.Recording.Enabled;
            SaveButton.IsEnabled = true;
            StatusText.Text = "";
        }
        catch (ConfigException ex)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = $"Config non leggibile: {ex.Message}";
            _logger?.Write("settings_load_failed", ex.Message);
        }
        catch (IOException ex)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = $"Config non leggibile: {ex.Message}";
            _logger?.Write("settings_load_failed", ex.Message);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        EndpointConfig endpoint = new()
        {
            Name = $"Endpoint {Rows.Count + 1}",
            Role = "secondary",
            Host = "127.0.0.1",
            Port = 8000,
        };

        Rows.Add(EndpointRowViewModel.From(endpoint));
        StatusText.Text = "Riga aggiunta: ricordati di salvare.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EndpointRowViewModel row })
        {
            Rows.Remove(row);
            StatusText.Text = "Riga rimossa: ricordati di salvare.";
        }
    }

    private async void VerifyNow_Click(object sender, RoutedEventArgs e)
    {
        if (_probe is null) return;
        StatusText.Text = "Verifica in corso…";

        foreach (EndpointRowViewModel row in Rows)
        {
            if (!row.IsValid(out string error))
            {
                row.ApplyProbeError(error);
                continue;
            }

            EndpointConfig endpoint = row.ToConfig();
            try
            {
                ProbeResult result = await _probe
                    .ProbeAsync(endpoint, _probeTimeout, CancellationToken.None)
                    .ConfigureAwait(true);
                row.ApplyProbe(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.ApplyProbeError("errore probe");
            }
        }

        StatusText.Text = "Verifica completata: nessun salvataggio, l'endpoint attivo non è cambiato.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        List<EndpointConfig> endpoints = [];
        foreach (EndpointRowViewModel row in Rows)
        {
            if (!row.IsValid(out string error))
            {
                StatusText.Text = $"Riga '{row.Name}': {error}.";
                return;
            }

            endpoints.Add(row.ToConfig());
        }

        if (endpoints.Count == 0)
        {
            StatusText.Text = "Serve almeno un endpoint.";
            return;
        }

        if (EndpointFormatPolicy.HasDuplicate(endpoints))
        {
            StatusText.Text = "Due righe puntano allo stesso host:porta.";
            return;
        }

        if (EndpointFormatPolicy.HasDuplicateName(endpoints))
        {
            StatusText.Text = "Due righe hanno lo stesso nome.";
            return;
        }

        int primary = endpoints.FindIndex(endpoint => endpoint.IsPrimary);
        if (primary < 0)
        {
            primary = 0;
        }

        EndpointFormatPolicy.SetPrimary(endpoints, primary);

        if (!ConfigWriter.SetEndpoints(_configPath, endpoints))
        {
            StatusText.Text = "Salvataggio non riuscito: la configurazione in memoria è invariata.";
            _logger?.Write("settings_save_failed", "ConfigWriter.SetEndpoints returned false");
            return;
        }

        _logger?.Write("settings_saved", $"{endpoints.Count} endpoint");

        _onSaved(endpoints);
        ReloadFromDisk();
        StatusText.Text = "Salvato. Il monitor riprova subito gli endpoint.";
    }

    public void SetRecordingStore(RecordingStore? store)
    {
        _recordingStore = store;
        _recordingStorage = store is null ? null : new RecordingStorage(store);
        if (_recordingStore is not null)
        {
            RecordingCheck.IsChecked = _recordingStore.Enabled;
        }
    }

    public void SetEndpointServices(
        EndpointSelector? selector,
        EndpointProbe? probe,
        Action<IReadOnlyList<EndpointConfig>>? onSaved = null)
    {
        _selector = selector;
        _probe = probe;
        if (onSaved is not null)
        {
            _onSaved = onSaved;
        }

        if (selector is not null)
        {
            ReloadFromDisk();
        }
    }

    public void ApplyModeView(string mode)
    {
        bool local = string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase);
        LocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        RemoteHeader.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        RemoteList.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        RemoteTelemetry.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        RemoteButtons.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        if (local)
        {
            UpdateLocal();
            _ = RefreshRecordingsAsync();
        }
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ModeCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string mode) return;
        if (!ConfigWriter.SetInferenceMode(_configPath, mode))
        {
            StatusText.Text = "Salvataggio modalità non riuscito: configurazione invariata.";
            return;
        }
        ApplyModeView(mode);
        _onModeSaved?.Invoke(mode);
        StatusText.Text = $"Modalità {mode} applicata a caldo.";
    }

    private void UpdateLocal()
    {
        LocalSettingsInfo info = _localInfo?.Invoke() ?? new("local", "vulkan", "—", "—", null);
        LocalBackendText.Text = info.Backend;
        LocalStateText.Text = info.State;
        LocalPairText.Text = info.Pair;
        LocalErrorText.Text = info.Error ?? "";
        UpdateModelStackButtons(info.ModelSelectionPath ?? info.Pair);
    }

    private void UpdateModelStackButtons(string? pathOrPair)
    {
        bool isMac = pathOrPair is not null && pathOrPair.Contains("parakeet", StringComparison.OrdinalIgnoreCase);
        StackDefaultBtn.BorderBrush = isMac ? System.Windows.Media.Brushes.Transparent : System.Windows.Media.Brushes.MediumSpringGreen;
        StackDefaultBtn.BorderThickness = isMac ? new Thickness(1) : new Thickness(2);
        StackMacBtn.BorderBrush = isMac ? System.Windows.Media.Brushes.MediumSpringGreen : System.Windows.Media.Brushes.Transparent;
        StackMacBtn.BorderThickness = isMac ? new Thickness(2) : new Thickness(1);
    }

    private void StackDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectModelPair("models/selected.json");
    }

    private void StackMacBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectModelPair("models/selected-parakeet-qwen.json");
    }

    private void SelectModelPair(string relativePath)
    {
        if (!ConfigWriter.SetModelSelectionPath(_configPath, relativePath))
        {
            StatusText.Text = "Salvataggio stack modelli non riuscito.";
            return;
        }
        UpdateModelStackButtons(relativePath);
        _onModelPairSaved?.Invoke(relativePath);
        StatusText.Text = $"Stack {Path.GetFileNameWithoutExtension(relativePath)} applicato a caldo.";
    }

    private void SwitchToRemoteBtn_Click(object sender, RoutedEventArgs e)
    {
        SetMode("remote");
        ApplyModeView("remote");
        if (ConfigWriter.SetInferenceMode(_configPath, "remote"))
        {
            _onModeSaved?.Invoke("remote");
            StatusText.Text = "Passato alla modalità Remoto (Mac).";
        }
    }

    private void SwitchToLocalBtn_Click(object sender, RoutedEventArgs e)
    {
        SetMode("local");
        ApplyModeView("local");
        if (ConfigWriter.SetInferenceMode(_configPath, "local"))
        {
            _onModeSaved?.Invoke("local");
            StatusText.Text = "Passato alla modalità Locale.";
        }
    }

    private async void RecordingCheck_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = RecordingCheck.IsChecked == true;
        if (!ConfigWriter.SetRecordingEnabled(_configPath, enabled))
        {
            RecordingCheck.IsChecked = !enabled;
            StatusText.Text = "Salvataggio del flag registrazione non riuscito.";
            return;
        }
        if (_recordingStore is not null) _recordingStore.Enabled = enabled;
        StatusText.Text = enabled
            ? "Registrazione attivata subito."
            : "Registrazione disattivata subito. I campioni esistenti restano sul disco.";
        await RefreshRecordingsAsync();
    }

    private async void DeleteRecordings_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingStorage is null) return;
        List<string> ids = [.. RecordingList.SelectedItems.Cast<string>()];
        if (ids.Count == 0)
        {
            StatusText.Text = "Seleziona almeno un campione da eliminare.";
            return;
        }
        try
        {
            await _recordingStorage.DeleteAsync(ids, CancellationToken.None);
            StatusText.Text = $"Eliminati {ids.Count} campioni.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = "Eliminazione non riuscita: " + error.Message;
        }
        await RefreshRecordingsAsync();
    }

    private async Task RefreshRecordingsAsync()
    {
        if (_recordingStorage is null || _recordingRefresh) return;
        _recordingRefresh = true;
        try
        {
            (int count, long bytes) = await _recordingStorage.GetSummaryAsync();
            IReadOnlyList<string> ids = await _recordingStorage.ListIdsAsync();
            RecordingSummaryText.Text = $"{count} campioni · {bytes / (1024.0 * 1024.0):0.0} MB";
            RecordingList.ItemsSource = ids;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            RecordingSummaryText.Text = "Campioni non leggibili: " + error.Message;
        }
        finally { _recordingRefresh = false; }
    }

    private void Refresh()
    {
        if (_selector is null)
        {
            UpdateLocal();
            return;
        }

        IReadOnlyList<EndpointStats> snapshot = _selector.Snapshot();
        var byKey = new Dictionary<string, EndpointStats>(StringComparer.OrdinalIgnoreCase);
        foreach (EndpointStats stats in snapshot)
        {
            byKey[stats.Endpoint.Key] = stats;
        }

        EndpointConfig active = _selector.Active;
        foreach (EndpointRowViewModel row in Rows)
        {
            string? currentKey = TryCurrentKey(row);
            EndpointStats? stats = null;

            if (currentKey is not null && byKey.TryGetValue(currentKey, out EndpointStats? current))
            {
                stats = current;
            }
            else if (row.OriginalKey is not null && byKey.TryGetValue(row.OriginalKey, out EndpointStats? original))
            {
                stats = original;
            }

            bool isActive = stats is not null && string.Equals(
                stats.Endpoint.Key, active.Key, StringComparison.OrdinalIgnoreCase);

            row.ApplyStats(stats, isActive);
        }

        EndpointStats? activeStats = snapshot.FirstOrDefault(stats =>
            string.Equals(stats.Endpoint.Key, active.Key, StringComparison.OrdinalIgnoreCase));

        SummaryText.Text = activeStats is null
            ? $"Auto · {active.Name}"
            : $"Auto · {active.Name} · {EndpointFormatPolicy.Latency(activeStats.MedianLatencyMs)}";

        if (_telemetry is not null)
        {
            TelemetryText.Text = FormatTelemetry(_telemetry.Snapshot());
        }
    }

    private static string? TryCurrentKey(EndpointRowViewModel row)
    {
        int? port = EndpointFormatPolicy.ParsePort(row.Port);
        if (port is null || !EndpointHost.IsValid(row.Host.Trim()))
        {
            return null;
        }

        return $"{row.Scheme}://{EndpointHost.ForUri(row.Host.Trim())}:{port}";
    }

    private static string FormatTelemetry(TelemetrySnapshot snapshot)
    {
        if (snapshot.LastEndpoint is null)
        {
            return "Nessuna dettatura registrata in questa sessione.";
        }

        string last = snapshot.LastDictationMs is null
            ? "ultima dettatura: —"
            : $"ultima dettatura: {EndpointFormatPolicy.Latency(snapshot.LastDictationMs)}";

        string handshake = snapshot.LastConnectMs is null
            ? "handshake: —"
            : $"handshake: {EndpointFormatPolicy.Latency(snapshot.LastConnectMs)}";

        string counters = $"OK {snapshot.Successes} · errori {snapshot.Failures} · retry {snapshot.Retries}";
        string endpoint = $"ultimo endpoint: {snapshot.LastEndpoint}";

        return snapshot.LastError is null
            ? $"{last} · {handshake} · {counters} · {endpoint}"
            : $"{last} · {handshake} · {counters} · {endpoint} · ultimo errore: {snapshot.LastError}";
    }
}
