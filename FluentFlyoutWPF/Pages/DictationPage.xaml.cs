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
/// Ajustes del dictado por voz (spec 006): encenderlo, su atajo —una tecla o una
/// combinación—, el idioma y los modelos locales.
///
/// <para>Los modelos son archivos ggml que se descargan una vez; el dictado corre
/// después en el equipo, sin red (RF-8). La lista mezcla el catálogo descargable con
/// los <c>.bin</c> que ya haya en la carpeta, para que un modelo añadido a mano (o
/// copiado desde otro equipo) aparezca igual.</para>
/// </summary>
public partial class DictationPage : Page
{
    private const string CudaDownloadsUrl = "https://developer.nvidia.com/cuda-downloads";

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
        UpdateGpuRuntimeStatus();
    }

    private void DictationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Se cierra la página (o la ventana de ajustes) con la caja quizá enfocada: el dictado
        // tiene que volver a escuchar sí o sí.
        EndHotkeyCapture();
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.Changed -= Dictation_Changed;
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

    // ------------------------------------------------------------------
    // Atajo
    // ------------------------------------------------------------------

    /// <summary>Teclas del atajo que el usuario mantiene ahora mismo en la caja.</summary>
    private readonly HashSet<int> _capturedKeys = [];
    private bool _hotkeyCapturing;

    /// <summary>Servicio del dictado (vive en la ventana principal), para avisarle de la captura.</summary>
    private static DictationService? Dictation =>
        Application.Current.MainWindow is MainWindow mainWindow ? mainWindow.Dictation : null;

    /// <summary>
    /// Captura del atajo: mientras la caja tiene el foco, cada tecla que baja se ACUMULA con
    /// las que ya estaban pulsadas y el ajuste guarda la combinación completa («Ctrl»,
    /// «Ctrl+Shift+M»…). Antes la combinación dependía de lo que dijera el estado del teclado
    /// de WPF en ese instante: si no reportaba los modificadores, cada tecla reemplazaba a la
    /// anterior y el atajo se quedaba en una sola tecla.
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
    /// Soltar una tecla no cambia el ajuste: se queda la última combinación completa, que es
    /// la que el usuario mantendrá para dictar. Solo deja de contar para lo que se pulse
    /// después (Ctrl + M y luego N acaba en «Ctrl+N», no en «Ctrl+M+N»).
    /// </summary>
    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        _capturedKeys.Remove(DictationHotkey.Normalize(VirtualKeyOf(e)));
    }

    private static int VirtualKeyOf(KeyEventArgs e) =>
        KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    /// <summary>Con el foco en la caja ya se puede definir el atajo: se para el dictado entero.</summary>
    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginHotkeyCapture();

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndHotkeyCapture();

    /// <summary>
    /// Mientras se define el atajo el dictado no escucha: pulsar Ctrl en la caja no debe abrir
    /// el micrófono, ni arrancar nada, ni cortar un dictado en marcha.
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
    /// Guarda el atajo con lo que el usuario mantiene AHORA (la caja enseña el ajuste, así que
    /// se ve al instante). Un ajuste ilegible no se guarda: se queda el que hubiera.
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
    /// Reconstruye la lista: catálogo (descargado o no) más los archivos sueltos de la
    /// carpeta. El modelo activo se marca con «En uso», y solo se ofrece descargar lo que
    /// falta y borrar lo que está.
    /// </summary>
    private void RefreshModels()
    {
        string configured = SettingsManager.Current.DictationModel;
        string? activePath = DictationModelStore.ResolveActivePath(configured);

        var rows = new List<DictationModelRow>();
        foreach (var model in DictationModelStore.Catalog)
        {
            rows.Add(new DictationModelRow(
                FileName: model.FileName,
                Name: model.Name,
                Subtitle: ModelSubtitle(model),
                FromCatalog: true,
                Installed: DictationModelStore.IsInstalled(model.FileName),
                Active: IsActive(configured, activePath, model.FileName)));
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
    }

    /// <summary>
    /// ¿Es el modelo activo? Vale el nombre del archivo y también su ruta completa: un
    /// modelo añadido a mano se guarda por ruta, no por nombre.
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
        return $"{model.Size} · {model.Language}{recommendation}";
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
            // El primero que se descarga pasa a ser el activo: sin modelo no hay dictado.
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
    /// Añadir un modelo del disco: se copia a la carpeta de modelos (ahí es donde el
    /// dictado los busca) y queda activo, que es lo que el usuario espera al elegirlo.
    /// </summary>
    private async void AddLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = IslandStrings.Get("DictationModelAddLocal", "Add local model"),
            Filter = IslandStrings.Get("DictationModelFileFilter",
                "Whisper model (*.bin)|*.bin|All files (*.*)|*.*"),
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

    /// <summary>Fila de la lista de modelos: es lo que la plantilla del XAML pinta.</summary>
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

        /// <summary>Progreso de la descarga (0-100).</summary>
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
