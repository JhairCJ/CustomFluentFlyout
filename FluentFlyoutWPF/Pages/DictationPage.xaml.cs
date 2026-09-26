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

        RefreshModels();
    }

    // ------------------------------------------------------------------
    // Atajo
    // ------------------------------------------------------------------

    /// <summary>
    /// Captura del atajo: al pulsar se escribe en la caja la combinación que el usuario
    /// mantiene (Ctrl, Ctrl+Shift+M…) y se guarda al instante. La tecla se captura en
    /// Preview para que no la consuma la navegación de la ventana.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        ApplyCapturedHotkey(vk);
    }

    /// <summary>
    /// Soltar una tecla vuelve a escribir lo que queda pulsado: así «Ctrl» se distingue de
    /// «Ctrl+Shift» sin cerrar nada, y soltar todo deja el atajo que de verdad se mantuvo.
    /// </summary>
    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e) => e.Handled = true;

    private void ApplyCapturedHotkey(int vk)
    {
        var keys = new List<int>();
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) keys.Add(DictationHotkey.VkCtrl);
        if (modifiers.HasFlag(ModifierKeys.Shift)) keys.Add(DictationHotkey.VkShift);
        if (modifiers.HasFlag(ModifierKeys.Alt)) keys.Add(DictationHotkey.VkAlt);
        if (modifiers.HasFlag(ModifierKeys.Windows)) keys.Add(DictationHotkey.VkWin);
        // La tecla que acaba de bajar cuenta también cuando es un modificador (WPF puede
        // no reportarlo aún en Keyboard.Modifiers) y siempre que no sea ya la misma.
        int normalized = DictationHotkey.Normalize(vk);
        if (normalized != 0 && !keys.Contains(normalized)) keys.Add(normalized);

        string text = DictationHotkey.Format(keys);
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
                Subtitle: $"{model.Size} · {model.Language}",
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
