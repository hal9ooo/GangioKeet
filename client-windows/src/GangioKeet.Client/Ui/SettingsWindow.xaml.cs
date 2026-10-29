using System.IO;
using System.Windows;
using System.Windows.Threading;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Impostazioni del client locale: stack modelli, registrazione campioni e
/// telemetria in sola lettura. Non esistono più modalità o endpoint remoti.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly TelemetryStore? _telemetry;
    private readonly string _configPath;
    private readonly FileLogger? _logger;
    private readonly Func<LocalSettingsInfo> _localInfo;
    private readonly Action<string>? _onModelPairSaved;
    private RecordingStore? _recordingStore;
    private RecordingStorage? _recordingStorage;
    private bool _recordingRefresh;
    private readonly DispatcherTimer _timer;

    public SettingsWindow(
        TelemetryStore? telemetry,
        string configPath,
        FileLogger? logger,
        Func<LocalSettingsInfo> localInfo,
        RecordingStore? recordingStore,
        Action<string>? onModelPairSaved)
    {
        _telemetry = telemetry;
        _configPath = configPath;
        _logger = logger;
        _localInfo = localInfo;
        _recordingStore = recordingStore;
        _recordingStorage = recordingStore is null ? null : new RecordingStorage(recordingStore);
        _onModelPairSaved = onModelPairSaved;

        InitializeComponent();
        UpdateLocal();

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher);
        _timer.Start();

        Loaded += (_, _) => ReloadFromDisk();
        Closed += (_, _) => _timer.Stop();
    }

    public bool UsesLocalServices => true;
    public bool IsLocalPanelVisible => LocalPanel.Visibility == Visibility.Visible;
    public string LocalState => LocalStateText.Text;
    public string LocalPair => LocalPairText.Text;

    private void LoadFromConfig(ClientConfig config)
    {
        UpdateModelStackButtons(config.Inference.ModelSelectionPath);
        UpdateLocal();
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
            StatusText.Text = "";
            _ = RefreshRecordingsAsync();
        }
        catch (ConfigException ex)
        {
            StatusText.Text = $"Config non leggibile: {ex.Message}";
            _logger?.Write("settings_load_failed", ex.Message);
        }
        catch (IOException ex)
        {
            StatusText.Text = $"Config non leggibile: {ex.Message}";
            _logger?.Write("settings_load_failed", ex.Message);
        }
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

    private void UpdateLocal()
    {
        LocalSettingsInfo info = _localInfo();        LocalBackendText.Text = info.Backend;
        LocalStateText.Text = info.State;
        LocalPairText.Text = info.Pair;
        LocalErrorText.Text = info.Error ?? "";
        UpdateModelStackButtons(info.ModelSelectionPath ?? info.Pair);
    }

    private void UpdateModelStackButtons(string? pathOrPair)
    {
        bool isParakeet = pathOrPair is not null
            && pathOrPair.Contains("parakeet", StringComparison.OrdinalIgnoreCase);
        StackDefaultBtn.BorderBrush = isParakeet
            ? System.Windows.Media.Brushes.Transparent
            : System.Windows.Media.Brushes.MediumSpringGreen;
        StackDefaultBtn.BorderThickness = isParakeet ? new Thickness(1) : new Thickness(2);
        StackParakeetBtn.BorderBrush = isParakeet
            ? System.Windows.Media.Brushes.MediumSpringGreen
            : System.Windows.Media.Brushes.Transparent;
        StackParakeetBtn.BorderThickness = isParakeet ? new Thickness(2) : new Thickness(1);
    }

    private void StackDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectModelPair("models/selected.json");
    }

    private void StackParakeetBtn_Click(object sender, RoutedEventArgs e)
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
        UpdateLocal();
        StatusText.Text = $"Stack {Path.GetFileNameWithoutExtension(relativePath)} applicato a caldo.";
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
        finally
        {
            _recordingRefresh = false;
        }
    }

    private void Refresh()
    {
        UpdateLocal();
        if (_telemetry is not null)
        {
            TelemetryText.Text = FormatTelemetry(_telemetry.Snapshot());
        }
    }

    private static string FormatTelemetry(TelemetrySnapshot snapshot)
    {
        if (snapshot.LastBackend is null)
        {
            return "Nessuna dettatura registrata in questa sessione.";
        }

        string last = snapshot.LastDictationMs is null
            ? "ultima dettatura: —"
            : $"ultima dettatura: {FormatMs(snapshot.LastDictationMs)}";
        string startup = snapshot.LastStartMs is null
            ? "avvio: —"
            : $"avvio: {FormatMs(snapshot.LastStartMs)}";
        string counters = $"OK {snapshot.Successes} · errori {snapshot.Failures} · retry {snapshot.Retries}";
        string backend = $"ultimo backend: {snapshot.LastBackend}";

        return snapshot.LastError is null
            ? $"{last} · {startup} · {counters} · {backend}"
            : $"{last} · {startup} · {counters} · {backend} · ultimo errore: {snapshot.LastError}";
    }

    private static string FormatMs(double? value) => $"{value.GetValueOrDefault():0} ms";
}
