using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
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
    public bool HasRetryAction => RetryModelsButton.Visibility == Visibility.Visible;
    public IReadOnlyList<string> AsrChoiceLabels =>
        AsrCombo.Items.Cast<ModelSelectionChoices.ModelChoice>().Select(choice => choice.Label).ToArray();
    public IReadOnlyList<string> CleanerChoiceLabels =>
        CleanerCombo.Items.Cast<ModelSelectionChoices.ModelChoice>().Select(choice => choice.Label).ToArray();

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
ModelsRootBox.Text = config.Inference.ModelsRoot ?? "";
            StatusText.Text = "Pronto.";
        StatusDot.Fill = (Brush)FindResource("Ok");
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
        LocalBackendText.Text = FriendlyBackend(info.Backend);
        LocalStateText.Text = FriendlyState(info.State);
        LocalPairText.Text = FriendlyPair(info.Pair);
        LocalErrorText.Text = info.Error ?? "";
LocalErrorText.Visibility = string.IsNullOrWhiteSpace(info.Error)
? Visibility.Collapsed
: Visibility.Visible;
StateDot.Fill = info.State switch
{
"Ready" => (Brush)FindResource("Ok"),
"Error" or "Failed" => (Brush)FindResource("Danger"),
_ => (Brush)FindResource("Muted"),
};
        if (_modelSelection is null)
        {
            ModelSelectionPanel.Visibility = Visibility.Collapsed;
            RetryModelsButton.Visibility = Visibility.Collapsed;
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
            RetryModelsButton.Visibility = Visibility.Collapsed;
            return;
        }

        ModelSelection current;
        try { current = _modelSelection.Current; }
        catch (InvalidOperationException) { return; }
        string key = ModelSelectionChoices.Key(current);
        if (!force && string.Equals(_lastSelectionKey, key, StringComparison.Ordinal)) return;

        ModelSelectionPanel.Visibility = Visibility.Visible;
        RetryModelsButton.Visibility = Visibility.Visible;
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
        try
        {
            bool needsDownload = !_modelSelection.IsAvailable(asr)
                || (refinement && cleaner is not null && !_modelSelection.IsAvailable(cleaner));
            if (needsDownload)
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    "I modelli selezionati non sono ancora sul disco. Scaricarli ora da Hugging Face?\n\n"
                    + "Nemotron 3.5 richiede circa 21 GB e molto tempo; gli altri modelli molto meno.",
                    "GangioKeet — download modelli", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Download annullato: resta attiva la combinazione precedente.";
                    return;
                }
            }

            ModelProgress.IsIndeterminate = true;
            ModelProgress.Value = 0;
            ModelProgress.Visibility = Visibility.Visible;
            ModelSelection desired = new(2, asr.Id, cleanerId, refinement);
            await _modelSelection.ApplyAsync(desired,
                new Progress<ModelProvisionProgress>(ShowProvisionProgress),
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

    private async void RetryModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modelSelection is null || _selectionApplying) return;
        _selectionApplying = true;
        RetryModelsButton.IsEnabled = false;
        ModelProgress.IsIndeterminate = true;
        ModelProgress.Value = 0;
        ModelProgress.Visibility = Visibility.Visible;
        try
        {
            await _modelSelection.RetryAsync(
                new Progress<ModelProvisionProgress>(ShowProvisionProgress),
                CancellationToken.None);
            StatusText.Text = "Riprova completata.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger?.Write("model_retry_failed", error.Message);
            StatusText.Text = "Riprova non riuscita: " + error.Message;
        }
        finally
        {
            _selectionApplying = false;
            RetryModelsButton.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
            UpdateLocal();
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
            RecordingSummaryText.Text = count == 0
                ? "Non hai ancora salvato nessuna dettatura."
                : $"{count} dettature salvate, {bytes / (1024.0 * 1024.0):0.0} MB";
            RecordingList.ItemsSource = ids;
            RecordingEmptyText.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void ShowProvisionProgress(ModelProvisionProgress progress)
    {
        string label = ModelLabel(progress.ModelId);
        string percent = "";
        string size = "";
        if (progress.TotalBytes is > 0)
        {
            double fraction = Math.Clamp(
                (double)progress.BytesTransferred / progress.TotalBytes.Value, 0, 1);
            percent = $" {fraction:P0}";
            size = $" ({FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes.Value)})";
        }

        StatusText.Text = progress.Status switch
        {
            ModelProvisionStatus.Checking => $"Verifica {label}…",
            ModelProvisionStatus.Downloading => $"Scaricamento {label}:{percent}{size}",
            ModelProvisionStatus.Completed => $"{label}: completato{size}",
            _ => $"{label}: {progress.Status}"
        };

        if (progress.Status == ModelProvisionStatus.Downloading && progress.TotalBytes is > 0)
        {
            ModelProgress.IsIndeterminate = false;
            ModelProgress.Value = Math.Clamp(
                (double)progress.BytesTransferred / progress.TotalBytes.Value, 0, 1);
        }
        else if (progress.Status == ModelProvisionStatus.Completed)
        {
            ModelProgress.IsIndeterminate = false;
            ModelProgress.Value = 1;
        }
        else
        {
            ModelProgress.IsIndeterminate = true;
        }
    }

    private string ModelLabel(string id)
    {
        if (id == "nemo-speech-runtime") return "runtime NeMo-Speech";
        if (_modelSelection is null) return id;
        ModelArtifact? artifact = _modelSelection.AsrCandidates
            .Concat(_modelSelection.CleanerCandidates)
            .FirstOrDefault(candidate => candidate.Id == id);
        return artifact is null ? id : ModelSelectionChoices.Label(artifact);
    }

    private static string FormatBytes(long bytes)
    {
        const long kilobyte = 1024;
        const long megabyte = kilobyte * 1024;
        const long gigabyte = megabyte * 1024;
        if (bytes >= gigabyte) return $"{bytes / (double)gigabyte:0.0} GB";
        if (bytes >= megabyte) return $"{bytes / (double)megabyte:0.0} MB";
        if (bytes >= kilobyte) return $"{bytes / (double)kilobyte:0.0} KB";
        return $"{bytes} B";
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

private void BrowseModelsRootButton_Click(object sender, RoutedEventArgs e)
{
OpenFolderDialog dialog = new() { Title = "Cartella dei modelli", Multiselect = false };
string current = ModelsRootBox.Text.Trim();
if (Directory.Exists(current)) dialog.InitialDirectory = current;
if (dialog.ShowDialog(this) == true) ModelsRootBox.Text = dialog.FolderName;
}

private void ApplyModelsRootButton_Click(object sender, RoutedEventArgs e)
{
string candidate = ModelsRootBox.Text.Trim();
if (candidate.Length == 0)
{
ShowModelsRootError("Indica una cartella. Senza percorso i modelli restano nella cartella del programma.");
return;
}

string full;
try
{
full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
}
catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
{
ShowModelsRootError("Percorso non valido: " + error.Message);
return;
}

if (!Directory.Exists(full))
{
ShowModelsRootError($"La cartella non esiste: {full}");
return;
}

bool hasModels;
try
{
hasModels = Directory.EnumerateDirectories(full).Any();
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
ShowModelsRootError("Non riesco a leggere la cartella: " + error.Message);
return;
}

if (!hasModels)
{
ShowModelsRootError("La cartella è vuota: scegli quella che contiene i modelli (parakeet_v3, gemma4_e2b, …).");
return;
}

if (!ConfigWriter.SetModelsRoot(_configPath, full))
{
ShowModelsRootError("Non sono riuscito a salvare la configurazione.");
return;
}

ModelsRootBox.Text = full;
LocalErrorText.Visibility = Visibility.Collapsed;
RestartButton.Visibility = Visibility.Visible;
StatusText.Text = "Cartella salvata. Riavvia GangioKeet per caricare i modelli da lì.";
}

private void RestartButton_Click(object sender, RoutedEventArgs e)
{
string? executable = Environment.ProcessPath;
if (string.IsNullOrEmpty(executable))
{
StatusText.Text = "Riavvio non disponibile: chiudi e riapri GangioKeet.";
return;
}

try
{
Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
}
catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
{
StatusText.Text = "Riavvio non riuscito: " + error.Message;
return;
}

Application.Current.Shutdown();
}

private void ShowModelsRootError(string message)
{
LocalErrorText.Text = message;
LocalErrorText.Visibility = Visibility.Visible;
StatusText.Text = message;
}

    // Etichette leggibili: i valori interni (vulkan, Ready, parakeet_v3) restano
    // nel config e nei log, ma nella pagina si mostrano in italiano.
    private static string FriendlyBackend(string backend) => backend switch
    {
        "vulkan" => "Vulkan (GPU)",
        "cuda" => "CUDA (NVIDIA)",
        "cpu" => "Solo processore",
        _ => backend,
    };

    private static string FriendlyState(string state) => state switch
    {
        "Ready" => "Pronto",
        "Starting" or "WarmingUp" => "Avvio in corso…",
        "Error" or "Failed" => "Non disponibile",
        _ => state,
    };

    private string FriendlyPair(string pair)
    {
        if (string.IsNullOrWhiteSpace(pair) || pair == "—") return "—";

        string[] parts = pair.Split(" + ", StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i] switch
            {
                "refinement off" => "rifinitura spenta",
                "nessuno" => "nessuna rifinitura",
                _ => DisplayNameOf(parts[i]),
            };
        }

        return string.Join(" + ", parts);
    }

    private string DisplayNameOf(string id)
    {
        if (_modelSelection is null) return id;

        foreach (ModelArtifact artifact in _modelSelection.AsrCandidates)
        {
            if (string.Equals(artifact.Id, id, StringComparison.Ordinal))
                return artifact.DisplayName ?? artifact.Id;
        }

        foreach (ModelArtifact artifact in _modelSelection.CleanerCandidates)
        {
            if (string.Equals(artifact.Id, id, StringComparison.Ordinal))
                return artifact.DisplayName ?? artifact.Id;
        }

        return id;
    }
}
