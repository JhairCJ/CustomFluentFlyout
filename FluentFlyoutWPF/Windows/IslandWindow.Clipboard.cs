// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Portapapeles del Island (change island-portapapeles): texto e imágenes copiados
/// por cualquier aplicación, listos para volver a pegarlos.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Captura por evento</b> (RF-1): la escucha nativa avisa de cada copia
/// (<see cref="IslandClipboardService"/>); el Island solo pinta lo que él publica.</item>
/// <item><b>Compacto = lo último</b> (RF-2): una fila con las últimas piezas (la
/// miniatura de la imagen o las primeras letras del texto) y el «+N» de las que no
/// caben, igual que el cajón y el estante.</item>
/// <item><b>Expandido = la lista</b> (RF-3): todas las piezas; al hacer clic en una
/// se copia al portapapeles y el usuario la pega donde quiera con Ctrl+V.</item>
/// <item><b>Sin actividad propia</b> (RF-5): como el cajón y el estante, no
/// reproduce ni cuenta: su vista la sostienen el puntero (Visible mientras activo)
/// o su aviso temporal.</item>
/// </list>
/// </summary>
public partial class IslandWindow
{
    private readonly IslandClipboardService _clipboard = new();
    /// <summary>Último aviso pintado en el expandido («Copiado al portapapeles»).</summary>
    private int _clipboardStatusVersion;

    /// <summary>Funcionalidad «portapapeles» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? ClipboardFeature => FeatureById(IslandFeatureIds.Clipboard);

    private bool ClipboardModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandClipboardEnabled;

    private bool ClipboardHasItems() => _clipboard.Items.Count > 0;

    /// <summary>
    /// El portapapeles no tiene actividad propia: en «Aviso temporal» su vista vive
    /// lo que vive su plazo y en «Visible mientras activo» la sostiene el puntero
    /// (misma regla que el cajón y el estante: 001 MOD RF-4/RF-9).
    /// </summary>
    private bool ClipboardKeepsView() =>
        ClipboardModeAvailable()
        && SettingsManager.Current.IslandVisibilityMode == 1
        && _noticeUntil > DateTime.UtcNow;

    internal IslandFeatureState GetClipboardFeatureState()
    {
        bool enabled = ClipboardModeAvailable();
        // Disponible con algo copiado: sin piezas no hay nada que pegar y el
        // contenedor no abre una vista vacía (001 MOD RF-9/RF-13).
        bool available = enabled && ClipboardHasItems();
        return new IslandFeatureState(enabled, available, Active: false,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Clipboard, Exclusive: false);
    }

    internal bool ShowClipboardExpandedFromContract()
    {
        if (!ClipboardModeAvailable() || !ClipboardHasItems()) return false;
        ExpandClipboard();
        return true;
    }

    internal bool ShowClipboardCompactFromContract()
    {
        if (!ClipboardModeAvailable() || !ClipboardHasItems()) return false;
        ShowClipboardCompact();
        return true;
    }

    /// <summary>
    /// Vincula la escucha del portapapeles: los avisos llegan en el hilo de UI (la
    /// ventana de mensajes vive en él), así que se puede pintar directamente.
    /// </summary>
    private void InitClipboard()
    {
        _clipboard.Changed += OnClipboardChanged;
        _clipboard.MaxItems = Math.Clamp(SettingsManager.Current.IslandClipboardMaxItems, 1, 100);
        if (ClipboardModeAvailable()) _clipboard.Start();
    }

    private void ShutdownClipboard()
    {
        _clipboard.Changed -= OnClipboardChanged;
        _clipboard.Dispose();
    }

    /// <summary>
    /// Ajuste de la funcionalidad en caliente: encendida arranca la escucha (y
    /// aprende el tope nuevo); apagada la para y repliega su vista si estaba puesta.
    /// </summary>
    public void RefreshClipboardContent() => Dispatcher.Invoke(() =>
    {
        _clipboard.MaxItems = Math.Clamp(SettingsManager.Current.IslandClipboardMaxItems, 1, 100);
        if (ClipboardModeAvailable()) _clipboard.Start();
        else
        {
            _clipboard.Stop();
            if (IsBoxShown && ViewShowsFeature(IslandFeatureIds.Clipboard)) FallbackFromClipboardView();
        }
        ApplyContentVisibility();
        SyncMeasuredHeight();
        PostActivity(IslandActivityReason.Clipboard | IslandActivityReason.Settings);
    });

    /// <summary>Vaciado desde ajustes o desde el propio Island.</summary>
    public void ClearClipboard() => Dispatcher.Invoke(() => _clipboard.Clear());

    private void OnClipboardChanged() => Dispatcher.Invoke(() =>
    {
        if (_disposed) return;
        // La lista cambió: se repinta lo que ya esté a la vista (nunca se despliega
        // solo: copiar no es un evento que deba abrir el Island).
        if (IsBoxShown && ViewShowsFeature(IslandFeatureIds.Clipboard))
        {
            ApplyContentVisibility();
            SyncMeasuredHeight();
        }
        UpdateArrows();
        PostActivity(IslandActivityReason.Clipboard);
    });

    /// <summary>Repinta la vista del portapapeles (lista y fila del compacto).</summary>
    private void RefreshClipboardViews()
    {
        var items = _clipboard.Items;
        int fit = IsNotch ? IslandClipboardItem.MaxCompactItemsNotch : IslandClipboardItem.MaxCompactItems;
        var compact = items.Take(fit).ToList();
        ClipboardCompactList.ItemsSource = compact;
        int rest = items.Count - compact.Count;
        ClipboardCompactMore.Text = rest > 0 ? $"+{rest}" : "";
        ClipboardCompactMore.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipboardExpandedList.ItemsSource = items;
        ClipboardExpandedEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipboardStatus.Text = "";
    }

    private void ShowClipboardCompact()
    {
        if (!ClipboardModeAvailable() || !ClipboardHasItems()) { SnapHidden(); return; }
        RefreshClipboardViews();
        ShowCompactView(IslandContentMode.Clipboard, ClipboardFeature, RefreshClipboardViews);
    }

    private void ExpandClipboard()
    {
        if (!ClipboardModeAvailable() || !ClipboardHasItems()) return;
        RefreshClipboardViews();
        ShowExpandedView(IslandContentMode.Clipboard, ClipboardFeature, RefreshClipboardViews);
    }

    /// <summary>
    /// La vista del portapapeles dejó de ser presentable (se apagó el ajuste): se
    /// repliega a la activa vigente o al reposo, sin dejar la superficie puesta.
    /// </summary>
    private void FallbackFromClipboardView()
    {
        // Con una pantalla delante, se recompone con sus miembros usables.
        if (RecoverScreensAfterMemberLost()) return;
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        HidePerMode();
    }

    // --- clics ---

    /// <summary>Clic en el compacto: abre la lista (001 MOD RF-3).</summary>
    private void ClipboardCompact_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExpandClipboard();
    }

    /// <summary>
    /// Clic en una pieza del expandido: se copia al portapapeles y el usuario la
    /// pega donde quiera con Ctrl+V (RF-3). El aviso «Copiado al portapapeles» se
    /// borra solo, sin tocar el resto de la vista.
    /// </summary>
    private void ClipboardItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not IslandClipboardItem item) return;
        _clipboard.Copy(item);
        ClipboardStatus.Text = item.Kind == IslandClipboardKind.Image
            ? "Imagen copiada · pégala con Ctrl+V"
            : "Texto copiado · pégalo con Ctrl+V";
        int version = ++_clipboardStatusVersion;
        _ = Task.Delay(2200).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_disposed || version != _clipboardStatusVersion) return;
            ClipboardStatus.Text = "";
        }));
    }
}
