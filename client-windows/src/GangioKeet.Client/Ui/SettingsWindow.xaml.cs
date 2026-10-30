using System.IO;
using System.Windows;
using System.Windows.Threading;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;
using GangioKeet.Inference;

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
    private readonly IModelSelectionHost? _modelSelection;
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _selectionTimer;
    private bool _populatingSelection;
    private bool _selectionApplying;
    private string? _lastSelectionKey;
    private RecordingStore? _recordingStore;
    private RecordingStorage? _recordingStorage;
    private bool _recordingRefresh;

    public SettingsWindow(
        TelemetryStore? telemetry,
        string configPath,
        FileLogger? logger,
        Func<LocalSettingsInfo> localInfo,
        RecordingStore? recordingStore,
        Action<string>? onModelPairSaved,
        IModelSelectionHost? modelSelection = null)
    {
        _telemetry = telemetry;
        _configPath = configPath;
        _logger = logger;
        _localInfo = localInfo;
        _recordingStore = recordingStore;
        _recordingStorage = recordingStore is null ? null : new RecordingStorage(recordingStore);
        _onModelPairSaved = onModelPairSaved;
        _modelSelection = modelSelection;

        InitializeComponent();
        UpdateLocal();

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher);
        _timer.Start();
        _selectionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _selectionTimer?.Stop();
                _ = ApplySelectionAsync();
            },
            Dispatcher);

        Loaded += (_, _) => ReloadFromDisk();
        Closed += (_, _) => { _timer.Stop(); _selectionTimer?.Stop(); };
    }

    public bool UsesLocalServices => true;
    public bool IsLocalPanelVisible => LocalPanel.Visibility == Visibility.Visible;
    public string LocalState => LocalStateText.Text;
    public string LocalPair => LocalPairText.Text;
    public bool HasModelSelection => ModelSelectionPanel.Visibility == Visibility.Visible;
    public IReadOnlyList<string> AsrChoiceLabels =>
        AsrCombo.Items.Cast<ModelSelectionChoices.ModelChoice>().Select(choice => choice.Label).ToArray();

    private void LoadFromConfig(ClientConfig config) => UpdateLocal();

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
        LocalSettingsInfo info = _localInfo();
        LocalBackendText.Text = info.Backend;
        LocalStateText.Text = info.State;
        LocalPairText.Text = info.Pair;
        LocalErrorText.Text = info.Error ?? "";
        if (_modelSelection is null)
        {
            ModelSelectionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ModelSelection current;
        try { current = _modelSelection.Current; }
        catch (InvalidOperationException) { return; }
        string key = ModelSelectionChoices.Key(current);
        if (ModelSelectionChoices.ShouldPopulate(_selectionApplying, _populatingSelection,
                _selectionTimer?.IsEnabled == true,
                AsrCombo.IsDropDownOpen || CleanerCombo.IsDropDownOpen,
                _lastSelectionKey, key))
            PopulateModelSelection();
    }

    private void PopulateModelSelection(bool force = false)
    {
        if (_modelSelection is null)
        {
            ModelSelectionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ModelSelection current;
        try { current = _modelSelection.Current; }
        catch (InvalidOperationException) { return; }
        string key = ModelSelectionChoices.Key(current);
        if (!force && string.Equals(_lastSelectionKey, key, StringComparison.Ordinal)) return;

        ModelSelectionPanel.Visibility = Visibility.Visible;
        _populatingSelection = true;
        try
        {
            if (force || AsrCombo.Items.Count == 0)
            {
                AsrCombo.ItemsSource = ModelSelectionChoices.AsrChoices(
                    _modelSelection.AsrCandidates, _modelSelection.IsAvailable);
                CleanerCombo.ItemsSource = ModelSelectionChoices.CleanerChoices(
                    _modelSelection.CleanerCandidates, _modelSelection.IsAvailable);
            }
            AsrCombo.SelectedItem = AsrCombo.Items.Cast<ModelSelectionChoices.ModelChoice>()
                .FirstOrDefault(choice => choice.Artifact.Id == current.SttId);
            CleanerCombo.SelectedItem = CleanerCombo.Items.Cast<ModelSelectionChoices.ModelChoice>()
                .FirstOrDefault(choice => choice.Artifact.Id == current.CleanerId);
            RefinementCheck.IsChecked = current.RefinementEnabled;
            CleanerCombo.IsEnabled = ModelSelectionChoices.CleanerEnabled(current.RefinementEnabled);
            _lastSelectionKey = key;
        }
        finally { _populatingSelection = false; }
    }

    private void AsrCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populatingSelection || _selectionApplying) return;
        ScheduleSelectionApply();
    }

    private void CleanerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populatingSelection || _selectionApplying) return;
        ScheduleSelectionApply();
    }

    private void RefinementCheck_Click(object sender, RoutedEventArgs e)
    {
        CleanerCombo.IsEnabled = ModelSelectionChoices.CleanerEnabled(RefinementCheck.IsChecked == true);
        ScheduleSelectionApply(immediate: true);
    }

    private void ScheduleSelectionApply(bool immediate = false)
    {
        if (_modelSelection is null || _selectionApplying) return;
        if (_selectionTimer is null)
        {
            if (immediate) _ = ApplySelectionAsync();
            return;
        }
        _selectionTimer.Stop();
        if (immediate) _ = ApplySelectionAsync();
        else _selectionTimer.Start();
    }

    private async Task ApplySelectionAsync()
    {
        if (_modelSelection is null || _selectionApplying) return;
        if (AsrCombo.SelectedItem is not ModelSelectionChoices.ModelChoice asrChoice) return;
        ModelArtifact asr = asrChoice.Artifact;
        ModelSelection current;
        try { current = _modelSelection.Current; }
        catch (InvalidOperationException) { return; }

        ModelArtifact? cleaner = (CleanerCombo.SelectedItem as ModelSelectionChoices.ModelChoice)?.Artifact;
        bool refinement = RefinementCheck.IsChecked == true;
        string? cleanerId = cleaner?.Id ?? current.CleanerId;
        if (refinement && string.IsNullOrWhiteSpace(cleanerId))
        {
            StatusText.Text = "Seleziona un cleaner per il refinement.";
            return;
        }

        _selectionApplying = true;
        AsrCombo.IsEnabled = false;
        CleanerCombo.IsEnabled = false;
        RefinementCheck.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        try
        {
            ModelSelection desired = new(2, asr.Id, cleanerId, refinement);
            await _modelSelection.ApplyAsync(desired,
                new Progress<ModelProvisionProgress>(p => StatusText.Text = $"Modelli: {p.Status}"),
                CancellationToken.None);
            StatusText.Text = "Combinazione applicata a caldo.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger?.Write("model_selection_apply_failed", error.Message);
            StatusText.Text = "Applicazione non riuscita: " + error.Message;
        }
        finally
        {
            _selectionApplying = false;
            ModelProgress.Visibility = Visibility.Collapsed;
            AsrCombo.IsEnabled = true;
            RefinementCheck.IsEnabled = true;
            CleanerCombo.IsEnabled = RefinementCheck.IsChecked == true;
            PopulateModelSelection(force: true);
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
