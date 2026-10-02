// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FluentFlyoutWPF.Pages;

/// <summary>
/// Voice dictation settings (spec 006): turning it on, its hotkey - one key or a
/// combination -, the language and the local models.
///
/// <para>Models are downloaded once; dictation then runs on the machine, with no
/// network (RF-8). The list mixes the downloadable catalog with local files, so a
/// hand-added model (or one copied from another machine) shows up the same way.</para>
/// </summary>
public partial class DictationPage : Page
{
    private const string CudaDownloadsUrl = "https://developer.nvidia.com/cuda-downloads";
    private const string NemoRuntimeGuideUrl = "https://github.com/NVIDIA/NeMo-Speech.cpp#installation";
    private const string NemoRuntimeInstallCommand = "irm https://github.com/NVIDIA/NeMo-Speech.cpp/raw/main/scripts/install.ps1 | iex";
    private const string QwenRuntimeGuideUrl = "https://github.com/QwenLM/Qwen3-ASR#environment-setup";
    private const string QwenRuntimeInstallCommand = "python -m pip install -U qwen-asr";

    private readonly ObservableCollection<DictationModelRow> _rows = [];
    private bool _loading;

    public DictationPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        ModelList.ItemsSource = _rows;

        _loading = true;
        LanguageCombo.SelectedIndex = SettingsManager.Current.DictationLanguage switch
        {
            "es" => 1,
            "en" => 2,
            _ => 0,
        };
        _loading = false;

        Loaded += DictationPage_Loaded;
        Unloaded += DictationPage_Unloaded;
        RefreshModels();
    }

    private void DictationPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.Changed += Dictation_Changed;
        DictationModelStore.DownloadStateChanged += DictationDownloadStateChanged;
        UpdateGpuRuntimeStatus();
        UpdateCrispAsrRuntimeStatus();
    }

    private void DictationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // The page (or the settings window) is closed with the box maybe still focused: dictation
        // has to start listening again no matter what.
        EndHotkeyCapture();
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.Changed -= Dictation_Changed;
        DictationModelStore.DownloadStateChanged -= DictationDownloadStateChanged;
    }

    private void DictationDownloadStateChanged(string fileName)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => DictationDownloadStateChanged(fileName));
            return;
        }

        RefreshModels();
    }

    private void Dictation_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateGpuRuntimeStatus);
            return;
        }

        UpdateGpuRuntimeStatus();
    }

    private void UpdateGpuRuntimeStatus()
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow) return;

        string? activePath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
        if (activePath != null
            && DictationModelStore.Find(Path.GetFileName(activePath))?.Backend == DictationModelBackend.CrispAsr)
        {
            bool loaded = mainWindow.Dictation.CrispModelLoaded;
            bool useGpu = loaded ? mainWindow.Dictation.CrispUsingGpu : SettingsManager.Current.DictationUseGpu;
            GpuRuntimeStatus.Text = useGpu
                ? loaded
                    ? IslandStrings.Get("DictationGpuStatusCrispVulkanLoaded", "Parakeet Ultra: Vulkan, model loaded (no CUDA)")
                    : IslandStrings.Get("DictationGpuStatusCrispVulkan", "Parakeet Ultra will use Vulkan (integrated or dedicated GPU, no CUDA)")
                : loaded
                    ? IslandStrings.Get("DictationGpuStatusCrispCpuLoaded", "Parakeet Ultra: CPU, model loaded (no CUDA)")
                    : IslandStrings.Get("DictationGpuStatusCrispCpu", "Parakeet Ultra will use CPU (no CUDA)");
            CudaDriverButton.Visibility = Visibility.Collapsed;
            return;
        }

        string key;
        string fallback;
        if (mainWindow.Dictation.AccelerationRestartRequired)
        {
            key = "DictationGpuRestartRequired";
            fallback = "Restart the app to apply the CPU/CUDA change";
        }
        else if (!mainWindow.Dictation.RuntimeLoaded)
        {
            key = "DictationGpuStatusNotLoaded";
            fallback = "Runtime: not loaded yet";
        }
        else if (mainWindow.Dictation.UsingGpuRuntime)
        {
            key = "DictationGpuStatusCuda";
            fallback = "Runtime: CUDA";
        }
        else
        {
            key = "DictationGpuStatusCpu";
            fallback = "Runtime: CPU (CUDA unavailable or disabled)";
        }

        GpuRuntimeStatus.Text = IslandStrings.Get(key, fallback);
        CudaDriverButton.Visibility = mainWindow.Dictation.RuntimeLoaded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DownloadCudaDrivers_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = CudaDownloadsUrl,
            UseShellExecute = true,
        });
    }

    private void InstallNemoRuntime_Click(object sender, RoutedEventArgs e)
    {
        PrepareExternalRuntimeInstall(NemoRuntimeGuideUrl, NemoRuntimeInstallCommand);
    }

    private void InstallQwenRuntime_Click(object sender, RoutedEventArgs e)
    {
        PrepareExternalRuntimeInstall(QwenRuntimeGuideUrl, QwenRuntimeInstallCommand);
    }

    /// <summary>
    /// CrispASR is the one runtime the application can install by itself: the Windows
    /// (Vulkan) build is a single 37.9 MB zip on GitHub, pinned by hash, that unpacks
    /// next to the models. It is what the Parakeet Ultra entries need.
    /// </summary>
    private async void DownloadCrispAsrRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (CrispAsrRuntime.IsInstalling) return;

        try
        {
            CrispAsrRuntimeButton.IsEnabled = false;
            CrispAsrRuntimeStatus.Text = IslandStrings.Get(
                "DictationRuntimeCrispAsrDownloading", "Downloading CrispASR…");
            var progress = new Progress<double>(value => CrispAsrRuntimeStatus.Text =
                IslandStrings.Format(
                    "DictationRuntimeCrispAsrDownloadingPercent",
                    "Downloading CrispASR… {0}%",
                    (int)(value * 100)));
            await CrispAsrRuntime.InstallAsync(progress);
            ModelsStatus.Text = IslandStrings.Get(
                "DictationRuntimeCrispAsrReady", "CrispASR installed");
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = IslandStrings.Format(
                "DictationRuntimeCrispAsrFailed", "Could not install CrispASR: {0}", ex.Message);
        }
        finally
        {
            CrispAsrRuntimeButton.IsEnabled = true;
            UpdateCrispAsrRuntimeStatus();
        }
    }

    private void UpdateCrispAsrRuntimeStatus()
    {
        bool installed = CrispAsrRuntime.IsInstalled;
        CrispAsrRuntimeButton.Content = installed
            ? IslandStrings.Get("DictationRuntimeCrispAsrReinstall", "Reinstall CrispASR (CPU / Vulkan)")
            : IslandStrings.Get("DictationRuntimeCrispAsr", "Download CrispASR (CPU / Vulkan)");
        CrispAsrRuntimeStatus.Text = installed
            ? IslandStrings.Format(
                "DictationRuntimeCrispAsrInstalled", "CrispASR {0} installed", CrispAsrRuntime.Version)
            : IslandStrings.Get("DictationRuntimeCrispAsrMissing", "CrispASR is not installed");
    }

    private void PrepareExternalRuntimeInstall(string guideUrl, string command)
    {
        try
        {
            Clipboard.SetText(command);
            ModelsStatus.Text = IslandStrings.Get(
                "DictationRuntimeCommandCopied",
                "Command copied; paste it into PowerShell or Terminal");
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = IslandStrings.Format(
                "DictationRuntimeCopyFailed",
                "Could not copy the command: {0}",
                ex.Message);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = guideUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = IslandStrings.Format(
                "DictationRuntimeOpenGuideFailed",
                "Could not open the guide: {0}",
                ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Atajo
    // ------------------------------------------------------------------

    /// <summary>Hotkey keys the user is holding right now in the box.</summary>
    private readonly HashSet<int> _capturedKeys = [];
    private bool _hotkeyCapturing;

    /// <summary>Dictation service (it lives in the main window), to tell it about the capture.</summary>
    private static DictationService? Dictation =>
        Application.Current.MainWindow is MainWindow mainWindow ? mainWindow.Dictation : null;

    /// <summary>
    /// Hotkey capture: while the box has focus, every key going down is ACCUMULATED with
    /// the ones already held and the setting saves the whole combination ("Ctrl",
    /// "Ctrl+Shift+M"...). The combination used to depend on whatever WPF's keyboard
    /// state reported at that instant: if it did not report modifiers, every key replaced
    /// the previous one and the hotkey stayed a single key.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // la tecla es del atajo, no de la navegación de la ventana
        BeginHotkeyCapture();
        int vk = DictationHotkey.Normalize(VirtualKeyOf(e));
        if (vk == 0) return;
        _capturedKeys.Add(vk);
        ApplyCapturedHotkey();
    }

    /// <summary>
    /// Releasing a key does not change the setting: it stays at the last whole combination,
    /// which is the one the user will hold to dictate. It only stops counting for whatever
    /// is pressed afterwards (Ctrl + M then N ends up as "Ctrl+N", not "Ctrl+M+N").
    /// </summary>
    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        _capturedKeys.Remove(DictationHotkey.Normalize(VirtualKeyOf(e)));
    }

    private static int VirtualKeyOf(KeyEventArgs e) =>
        KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    /// <summary>With the box focused the hotkey can already be defined: the whole dictation service is stopped.</summary>
    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginHotkeyCapture();

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndHotkeyCapture();

    /// <summary>
    /// While the hotkey is being defined dictation does not listen: pressing Ctrl in the box
    /// must not open the microphone, start anything, or cut a dictation in progress.
    /// </summary>
    private void BeginHotkeyCapture()
    {
        if (_hotkeyCapturing) return;
        _hotkeyCapturing = true;
        _capturedKeys.Clear();
        if (Dictation is { } dictation) dictation.HotkeyCaptureActive = true;
    }

    private void EndHotkeyCapture()
    {
        if (!_hotkeyCapturing) return;
        _hotkeyCapturing = false;
        _capturedKeys.Clear();
        if (Dictation is { } dictation) dictation.HotkeyCaptureActive = false;
    }

    /// <summary>
    /// Saves the hotkey with what the user is holding RIGHT NOW (the box shows the setting,
    /// so it is visible immediately). An unreadable setting is not saved: the previous one stays.
    /// </summary>
    private void ApplyCapturedHotkey()
    {
        string text = DictationHotkey.Format(_capturedKeys);
        if (DictationHotkey.IsValid(text)) SettingsManager.Current.DictationHotkey = text;
    }

    // ------------------------------------------------------------------
    // Idioma
    // ------------------------------------------------------------------

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        SettingsManager.Current.DictationLanguage = LanguageCombo.SelectedIndex switch
        {
            1 => "es",
            2 => "en",
            _ => "auto",
        };
    }

    // ------------------------------------------------------------------
    // Modelos
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the list: catalog (downloaded or not) plus the loose files in the folder.
    /// The active model is marked "In use", and only download what is missing and delete
    /// what is present are offered.
    /// </summary>
    private void RefreshModels()
    {
        string configured = SettingsManager.Current.DictationModel;
        string? activePath = DictationModelStore.ResolveActivePath(configured);

        var rows = new List<DictationModelRow>();
        foreach (var model in DictationModelStore.Catalog)
        {
            var row = new DictationModelRow(
                FileName: model.FileName,
                Name: model.Name,
                Subtitle: ModelSubtitle(model),
                FromCatalog: true,
                Installed: DictationModelStore.IsInstalled(model.FileName),
                Active: IsActive(configured, activePath, model.FileName));
            if (DictationModelStore.IsDownloading(model.FileName))
            {
                row.Busy = true;
                row.Subtitle = IslandStrings.Get("DictationModelDownloading", "Downloading…");
            }
            rows.Add(row);
        }
        foreach (string file in DictationModelStore.InstalledFiles())
        {
            if (DictationModelStore.Find(file) != null) continue; // ya está en el catálogo
            rows.Add(new DictationModelRow(
                FileName: file,
                Name: Path.GetFileNameWithoutExtension(file),
                Subtitle: IslandStrings.Get("DictationModelAddedByHand", "Model added by hand"),
                FromCatalog: false,
                Installed: true,
                Active: IsActive(configured, activePath, file)));
        }

        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);
        UpdateGpuRuntimeStatus();
    }

    /// <summary>
    /// Is it the active model? The file name counts and so does its full path: a
    /// hand-added model is saved by path, not by name.
    /// </summary>
    private static bool IsActive(string configured, string? activePath, string fileName) =>
        string.Equals(configured, fileName, StringComparison.OrdinalIgnoreCase)
        || (activePath != null
            && string.Equals(Path.GetFileName(activePath), fileName, StringComparison.OrdinalIgnoreCase));

    private static string ModelSubtitle(DictationModelInfo model)
    {
        string recommendation = model.Recommended
            ? $" · {IslandStrings.Get("DictationModelRecommended", "Recommended")}"
            : "";
        string runtime = model.Backend == DictationModelBackend.CrispAsr
            ? $" · {IslandStrings.Get("DictationRuntimeCrispAsrCpu", "CrispASR · CPU / Vulkan · no CUDA")}"
            : string.IsNullOrWhiteSpace(model.Runtime) ? "" : $" · {model.Runtime}";
        return $"{model.Size} · {model.Language}{runtime}{recommendation}";
    }

    private void UseModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        SettingsManager.Current.DictationModel = row.FileName;
        ModelsStatus.Text = "";
        RefreshModels();
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        if (DictationModelStore.Find(row.FileName) is not { } model) return;
        if (DictationModelStore.IsDownloading(row.FileName)) return;

        try
        {
            row.Busy = true;
            row.Subtitle = IslandStrings.Get("DictationModelDownloading", "Downloading…");
            var progress = new Progress<double>(value =>
            {
                row.Progress = value * 100;
                row.Subtitle = IslandStrings.Format("DictationModelDownloadingPercent", "Downloading… {0}%",
                    (int)(value * 100));
            });
            await DictationModelStore.DownloadAsync(model, progress);
            row.Busy = false;
            row.Installed = true;
            ModelsStatus.Text = IslandStrings.Get("DictationModelReady", "Model ready");
            // The first one downloaded becomes the active one: without a model there is no dictation.
            if (string.IsNullOrWhiteSpace(SettingsManager.Current.DictationModel))
                SettingsManager.Current.DictationModel = row.FileName;
            RefreshModels();
        }
        catch (Exception ex)
        {
            row.Busy = false;
            ModelsStatus.Text = IslandStrings.Format("DictationModelDownloadFailed", "Download failed: {0}", ex.Message);
            RefreshModels();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        DictationModelStore.Delete(row.FileName);
        if (string.Equals(SettingsManager.Current.DictationModel, row.FileName, StringComparison.OrdinalIgnoreCase))
            SettingsManager.Current.DictationModel = "";
        ModelsStatus.Text = "";
        RefreshModels();
    }

    /// <summary>
    /// Add a model from disk: it is copied into the models folder (that is where dictation
    /// looks for them) and becomes active, which is what the user expects when picking one.
    /// </summary>
    private async void AddLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = IslandStrings.Get("DictationModelAddLocal", "Add local model"),
            Filter = IslandStrings.Get("DictationModelFileFilter",
                "Whisper/Parakeet model (*.bin;*.gguf)|*.bin;*.gguf|All files (*.*)|*.*"),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            string path = await DictationModelStore.AddLocalAsync(dialog.FileName);
            SettingsManager.Current.DictationModel = Path.GetFileName(path);
            ModelsStatus.Text = IslandStrings.Get("DictationModelReady", "Model ready");
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = IslandStrings.Format("DictationModelCopyFailed", "Could not add the model: {0}", ex.Message);
        }
        RefreshModels();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DictationModelStore.ModelsFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = DictationModelStore.ModelsFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = ex.Message;
        }
    }

    /// <summary>A row of the model list: what the XAML template draws.</summary>
    public partial class DictationModelRow : ObservableObject
    {
        public DictationModelRow(string FileName, string Name, string Subtitle, bool FromCatalog,
            bool Installed, bool Active)
        {
            this.FileName = FileName;
            this.Name = Name;
            this.Subtitle = Subtitle;
            this.FromCatalog = FromCatalog;
            this.Installed = Installed;
            this.Active = Active;
        }

        public string FileName { get; }
        public string Name { get; }
        public bool FromCatalog { get; }

        [ObservableProperty]
        public partial string Subtitle { get; set; }

        [ObservableProperty]
        public partial bool Installed { get; set; }

        [ObservableProperty]
        public partial bool Active { get; set; }

        [ObservableProperty]
        public partial bool Busy { get; set; }

        /// <summary>Download progress (0-100).</summary>
        [ObservableProperty]
        public partial double Progress { get; set; }

        public FontWeight NameWeight => Active ? FontWeights.SemiBold : FontWeights.Normal;

        public bool CanUse => Installed && !Active && !Busy;
        public bool CanDownload => FromCatalog && !Installed && !Busy;
        public bool CanDelete => Installed && !Busy;

        partial void OnActiveChanged(bool value) => OnPropertyChanged(nameof(NameWeight));
        partial void OnInstalledChanged(bool value) => NotifyActions();
        partial void OnBusyChanged(bool value) => NotifyActions();

        private void NotifyActions()
        {
            OnPropertyChanged(nameof(CanUse));
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(CanDelete));
        }
    }
}
