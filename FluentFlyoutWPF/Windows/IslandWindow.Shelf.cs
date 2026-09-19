// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Estante de archivos del Island: quinto contenido del contenedor, junto a música,
/// temporizador y cajón de aplicaciones.
///
/// <para>Soltar archivos o carpetas encima del Island los aparca (se mueven a la
/// carpeta propia del estante) y arrastrar un elemento fuera lo saca. Los elementos no
/// caducan: solo salen cuando el usuario los arrastra fuera o los quita, y quitarlos
/// los devuelve a su carpeta original — desde aquí NUNCA se borra nada del usuario.</para>
///
/// <para>Como el cajón, el estante no genera actividad propia: su vista la sostienen el
/// puntero (Visible mientras activo) o el plazo del aviso temporal (001 MOD RF-4/RF-9,
/// 002 RF-8).</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Elementos del estante (nunca null tras el arranque).</summary>
    private static ObservableCollection<IslandShelfItem> ShelfItems => SettingsManager.Current.IslandShelfItems;

    /// <summary>
    /// ¿El estante se puede mostrar ahora? Basta con tenerlo habilitado: a diferencia
    /// del cajón, el estante VACÍO es una vista válida —es la invitación a soltar algo
    /// encima—, así que no depende de tener elementos (001 MOD RF-9).
    /// </summary>
    private bool ShelfModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandShelfEnabled;

    /// <summary>
    /// El estante sostiene su vista mientras esté disponible y, en «Aviso temporal»,
    /// mientras viva el plazo de su aviso (001 RF-2). En «Visible mientras activo» no
    /// genera actividad propia: su vista la sostiene el puntero y se repliega al
    /// apartarlo, como cualquier otro contenido sin actividad.
    /// </summary>
    private bool ShelfKeepsView() =>
        ShelfModeAvailable()
        && SettingsManager.Current.IslandVisibilityMode == 1
        && _noticeUntil > DateTime.UtcNow;

    internal IslandFeatureState GetShelfFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandShelfEnabled;
        return new IslandFeatureState(enabled, enabled, Active: false,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Shelf, Exclusive: false);
    }

    internal bool ShowShelfExpandedFromContract()
    {
        // La alerta final del temporizador es exclusiva: no se sustituye (002 RF-2).
        if (HasExclusive() || !ShelfModeAvailable()) return false;
        ExpandShelf();
        return true;
    }

    internal bool ShowShelfCompactFromContract()
    {
        if (!ShelfModeAvailable()) return false;
        ShowShelfCompact();
        return true;
    }

    /// <summary>Funcionalidad «estante» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? ShelfFeature => FeatureById(IslandFeatureIds.Shelf);

    /// <summary>
    /// Vincula las listas del estante. El compacto lleva solo los que caben en la fila
    /// (el resto vive en el expandido, contados por el «+N»), igual que el cajón.
    /// </summary>
    private void InitShelf() => RefreshShelfList();

    private void RefreshShelfList()
    {
        var items = ShelfItems;
        // El notch es más estrecho por diseño: caben menos mosaicos en su renglón. La
        // celda es la misma que la del cajón (20 + 4 de paso), así que encajan los
        // mismos que allí: 6 en el notch y 7 en el pill.
        int fit = IsNotch ? IslandShelfItem.MaxCompactItemsNotch : IslandShelfItem.MaxCompactItems;
        var compact = items.Take(fit).ToList();
        ShelfCompactList.ItemsSource = compact;
        ShelfExpandedList.ItemsSource = items;
        int rest = items.Count - compact.Count;
        ShelfCompactMore.Text = rest > 0 ? $"+{rest}" : "";
        ShelfCompactMore.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfExpandedEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ajuste del estante en caliente: con el estante apagado la vista no puede quedarse
    /// en él; con él utilizable, las listas se re-vinculan.
    /// </summary>
    public void RefreshShelfContent() => Dispatcher.Invoke(() =>
    {
        RefreshShelfList();
        if (ShelfModeAvailable())
        {
            if (IsBoxShown && _contentMode == IslandContentMode.Shelf)
            {
                ApplyContentVisibility();
                SyncMeasuredHeight();
            }
            UpdateArrows();
            return;
        }
        if (_contentMode == IslandContentMode.Shelf) FallbackFromShelfView();
        UpdateArrows();
    });

    /// <summary>
    /// El estante dejó de ser usable con su vista puesta (se apagó en ajustes): se
    /// repliega a la activa vigente o al reposo, sin dejar una superficie vacía a la
    /// vista.
    /// </summary>
    private void FallbackFromShelfView()
    {
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && vigente.TryShowCompact())
            return;
        _expanded = false;
        HidePerMode();
    }

    private void ExpandShelf() =>
        ShowExpandedView(IslandContentMode.Shelf, ShelfFeature, RefreshShelfList);

    private void ShowShelfCompact()
    {
        if (!ShelfModeAvailable()) { SnapHidden(); return; }
        // «Aviso temporal»: el estante es un aviso como los demás y también vence
        // (001 RF-2): se repliega al plazo configurado en vez de quedarse pegado.
        ShowCompactView(IslandContentMode.Shelf, ShelfFeature, RefreshShelfList);
    }

    // --- soltar encima del Island (drop) ---

    /// <summary>
    /// ¿El Island acepta este arrastre? Solo con el estante habilitado y con algo vivo
    /// que mover: el Island no finge ser un destino si luego no guarda nada.
    /// </summary>
    private bool CanAcceptShelfDrop(IDataObject? data)
    {
        if (data == null || !ShelfModeAvailable()) return false;
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return false;
        if (ShelfItems.Count >= IslandShelfItem.MaxItems) return false;
        return paths.Any(p => File.Exists(p) || Directory.Exists(p));
    }

    /// <summary>
    /// Algo arrastrado sobre el Island: el estante lo acepta y el borde se ilumina para
    /// que se vea que el sitio está disponible. Fuera del estante el Island no acepta el
    /// gesto: nada de soltar archivos en un contenedor que los ignoraría.
    /// </summary>
    private void IslandBox_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool accepted = CanAcceptShelfDrop(e.Data);
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        if (accepted == _shelfDropHot) return;
        _shelfDropHot = accepted;
        // ApplyStyle es quien decide el color del borde (con o sin resaltado).
        ApplyStyle();
    }

    private void IslandBox_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_shelfDropHot) return;
        _shelfDropHot = false;
        ApplyStyle();
    }

    /// <summary>
    /// Suelta archivos o carpetas encima del Island: cada uno se MUEVE a la carpeta del
    /// estante (nada se copia, nada se pisa: si el nombre está cogido se numera) y el
    /// estante se muestra con lo que acaba de guardar — el gesto tiene que verse.
    /// </summary>
    private void IslandBox_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_shelfDropHot)
        {
            _shelfDropHot = false;
            ApplyStyle();
        }
        if (!CanAcceptShelfDrop(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (SettingsManager.Current.AddIslandShelfPaths(paths) <= 0) return;
        RefreshShelfList();
        ExpandShelf();
    }

    // --- arrastrar un elemento fuera del Island ---

    // Origen del gesto y elemento candidato: el arrastre real empieza al moverse, no al
    // pulsar (si no, un clic para quitar arrastraría sin querer).
    private Point _shelfDragOrigin;
    private IslandShelfItem? _shelfDragCandidate;

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IslandShelfItem item) return;
        _shelfDragCandidate = item;
        _shelfDragOrigin = e.GetPosition(this);
    }

    /// <summary>
    /// Saca un elemento del estante: sale con el mismo gesto con el que entró. El efecto
    /// ofrecido es Move (el destino se lo lleva); si el destino copia, el elemento sigue
    /// en el estante, y si se lo lleva, al volver el archivo ya no está donde estaba y
    /// el estante deja de contarlo. Sin borrar nada: el archivo se fue a otro sitio.
    /// </summary>
    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        var item = _shelfDragCandidate;
        if (item == null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _shelfDragOrigin.X) < 6 && Math.Abs(now.Y - _shelfDragOrigin.Y) < 6) return;
        _shelfDragCandidate = null;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Move | DragDropEffects.Copy);
            if (effect == DragDropEffects.None || item.Exists || !ShelfItems.Contains(item)) return;
            ShelfItems.Remove(item);
            RefreshShelfList();
            if (_contentMode == IslandContentMode.Shelf)
            {
                ApplyContentVisibility();
                SyncMeasuredHeight();
            }
        }
        catch (Exception ex)
        {
            // Ruta borrada, sin permisos o gesto cancelado: se registra y el elemento
            // sigue en el estante.
            Logger.Warn(ex, "Island: no se pudo arrastrar {Path} fuera del estante", item.Path);
        }
    }

    /// <summary>
    /// Quita un elemento del estante devolviéndolo a su carpeta original (ver
    /// <c>UserSettings.RemoveIslandShelfItem</c>): quitarlo del estante no puede destruir
    /// nada del usuario. Si la carpeta original ya no existe, el elemento se queda.
    /// </summary>
    private void ShelfItemRemove_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _shelfDragCandidate = null;
        if ((sender as FrameworkElement)?.DataContext is not IslandShelfItem item) return;
        if (!SettingsManager.Current.RemoveIslandShelfItem(item)) return;
        RefreshShelfList();
        if (_contentMode != IslandContentMode.Shelf) return;
        ApplyContentVisibility();
        SyncMeasuredHeight();
    }
}
