// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Cajón de aplicaciones del Island: tercer contenido del contenedor junto a
/// música y temporizador. Las aplicaciones se configuran en ajustes; el compacto
/// muestra una fila de iconos y el expandido la cuadrícula completa, y un clic
/// en cualquier icono lanza su aplicación.
///
/// <para>El cajón no genera actividad propia (no reproduce ni cuenta): por eso
/// declara <see cref="IslandFeatureState.Active"/> en false y se repliega con el
/// puntero (Visible mientras activo) o con el plazo del aviso temporal
/// (001 MOD RF-4/RF-9, 002 RF-8).</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Lista configurada en ajustes (nunca null tras el arranque).</summary>
    private static ObservableCollection<IslandApp> Apps => SettingsManager.Current.IslandApps;

    /// <summary>
    /// ¿El cajón se puede mostrar ahora? Habilitado en ajustes y con al menos una
    /// aplicación: sin aplicaciones no hay vista que abrir (001 MOD RF-9).
    /// </summary>
    private bool AppsModeAvailable() =>
        SettingsManager.Current.IslandEnabled
        && SettingsManager.Current.IslandAppsEnabled
        && SettingsManager.Current.IslandApps.Count > 0;

    /// <summary>
    /// El cajón sostiene su vista mientras esté disponible y, en «Aviso
    /// temporal», mientras viva el plazo de su aviso (001 RF-2). En «Visible
    /// mientras activo» no genera actividad propia: su vista la sostiene el
    /// puntero y se repliega al apartarlo, como cualquier otro contenido.
    /// </summary>
    private bool AppsKeepsView() =>
        AppsModeAvailable()
        && SettingsManager.Current.IslandVisibilityMode == 1
        && _noticeUntil > DateTime.UtcNow;

    internal IslandFeatureState GetAppsFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandAppsEnabled;
        bool available = enabled && SettingsManager.Current.IslandApps.Count > 0;
        return new IslandFeatureState(enabled, available, Active: false,
            Selected: _selectedFeature?.Id == "apps", Exclusive: false);
    }

    internal bool ShowAppsExpandedFromContract()
    {
        // La alerta final del temporizador es exclusiva: no se sustituye (002 RF-2).
        if (HasExclusive() || !AppsModeAvailable()) return false;
        ExpandApps();
        return true;
    }

    internal bool ShowAppsCompactFromContract()
    {
        if (!AppsModeAvailable()) return false;
        ShowAppsCompact();
        return true;
    }

    /// <summary>
    /// Vincula las listas del cajón a las aplicaciones configuradas. El compacto
    /// lleva solo las que caben en la fila (el resto vive en el expandido) para
    /// que ninguna quede recortada por el ancho del renglón.
    /// </summary>
    private void InitApps() => RefreshAppList();

    private void RefreshAppList()
    {
        var apps = SettingsManager.Current.IslandApps;
        // El notch es más estrecho por diseño: caben menos iconos en su renglón.
        int fit = IsNotch ? 5 : IslandApp.MaxCompactApps;
        var compact = apps.Take(fit).ToList();
        AppsCompactList.ItemsSource = compact;
        AppsExpandedList.ItemsSource = apps;
        int rest = apps.Count - compact.Count;
        AppsCompactMore.Text = rest > 0 ? $"+{rest}" : "";
        AppsCompactMore.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ajuste del cajón en caliente: con el cajón apagado (o sin aplicaciones) la
    /// vista no puede quedarse en él; con él utilizable, las listas se re-vinculan.
    /// </summary>
    public void RefreshAppsContent() => Dispatcher.Invoke(() =>
    {
        RefreshAppList();
        if (AppsModeAvailable())
        {
            if (IsBoxShown && _contentMode == IslandContentMode.Apps)
            {
                ApplyContentVisibility();
                SyncMeasuredHeight();
            }
            UpdateArrows();
            return;
        }
        if (_contentMode == IslandContentMode.Apps) FallbackFromAppsView();
        UpdateArrows();
    });

    /// <summary>
    /// El cajón dejó de ser usable con su vista puesta (se apagó en ajustes o se
    /// quedó sin aplicaciones): se repliega a la activa vigente o al reposo, sin
    /// dejar una superficie vacía a la vista.
    /// </summary>
    private void FallbackFromAppsView()
    {
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && vigente.TryShowCompact())
            return;
        _expanded = false;
        HidePerMode();
    }

    private void ExpandApps()
    {
        ShowExpandedView(IslandContentMode.Apps, AppsFeature, RefreshAppList);
    }

    private void ShowAppsCompact()
    {
        if (!AppsModeAvailable()) { SnapHidden(); return; }
        // «Aviso temporal»: el cajón es un aviso como los demás y también vence
        // (001 RF-2): se repliega al plazo configurado en vez de quedarse pegado.
        ShowCompactView(IslandContentMode.Apps, AppsFeature, RefreshAppList);
    }

    /// <summary>Funcionalidad «cajón de aplicaciones» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? AppsFeature => FeatureById("apps");

    // --- interacción ---

    /// <summary>Clic en un icono del cajón (compacto o expandido): lanza su aplicación.</summary>
    private void AppLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        // El clic es del icono: no debe llegar al manejador de la caja (que
        // expandiría la última usable).
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not IslandApp app) return;
        LaunchApp(app);
    }

    private void LaunchApp(IslandApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app.Path,
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path) ?? "",
            });
        }
        catch (Exception ex)
        {
            // Ruta borrada, sin permisos o acceso directo roto: se registra y no
            // se deja el Island abierto sobre un fallo silencioso.
            Logger.Warn(ex, "Island: no se pudo lanzar la aplicación {App} ({Path})", app.Name, app.Path);
        }
        RetractAfterAppLaunch();
    }

    /// <summary>
    /// Tras lanzar una aplicación el Island se contrae (como el cierre deliberado
    /// de la cuenta del temporizador): no se queda tapando la ventana que acaba
    /// de abrir ni vuelve solo por el hover, que sigue encima tras el clic
    /// (001 MOD RF-4).
    /// </summary>
    private void RetractAfterAppLaunch()
    {
        _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        _expanded = false;
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && vigente.TryShowCompact())
            return;
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        ShowInactiveOrHidden();
    }
}
