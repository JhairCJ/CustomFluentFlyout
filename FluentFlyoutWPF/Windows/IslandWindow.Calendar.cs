// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Recordatorios de Google Calendar en el Island: cuarto contenido del contenedor,
/// junto a música, temporizador, cajón de aplicaciones y estante.
///
/// <para>A diferencia del cajón o el estante, el calendario SÍ tiene actividad propia:
/// mientras un evento está en su ventana de recordatorio (sus últimos minutos antes de
/// empezar, más dos de gracia) la funcionalidad está activa y su vista se sostiene sola
/// en los dos modos de visibilidad (001 MOD RF-4). Al entrar en la ventana, el
/// contenedor se despliega con el aviso: un recordatorio que hay que ir a buscar no
/// recuerda nada.</para>
///
/// <para>El trabajo de leer el calendario lo lleva <see cref="GoogleCalendarService"/>
/// en segundo plano; aquí solo se pinta lo que él publica y se decide cuándo
/// enseñarlo.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Recordatorio vigente (el evento que desplegó el aviso).</summary>
    private GoogleCalendarEvent? _calendarReminder;

    /// <summary>Funcionalidad «calendario» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? CalendarFeature => FeatureById(IslandFeatureIds.Calendar);

    /// <summary>
    /// ¿El calendario se puede mostrar ahora? Como el cajón: sin sesión no hay nada que
    /// enseñar, así que la funcionalidad no es usable y no aparece en la navegación (una
    /// vista vacía no vale como funcionalidad). La sesión se inicia desde ajustes.
    /// </summary>
    private bool CalendarModeAvailable() =>
        SettingsManager.Current.IslandEnabled
        && SettingsManager.Current.IslandCalendarEnabled
        && SettingsManager.Current.GoogleCalendarSignedIn;

    /// <summary>
    /// ¿Está vivo el recordatorio? Desde los minutos configurados antes del comienzo y
    /// hasta dos minutos después: pasado ese punto el aviso ya no recuerda nada.
    /// </summary>
    private bool CalendarReminderLive()
    {
        var item = _calendarReminder;
        if (item == null) return false;
        DateTime now = DateTime.Now;
        return now >= item.Start.AddMinutes(-Math.Clamp(SettingsManager.Current.GoogleCalendarReminderMinutes, 1, 60))
            && now <= item.Start.AddMinutes(2);
    }

    /// <summary>
    /// El calendario sostiene su vista él solo mientras el recordatorio esté vivo: es
    /// la única funcionalidad del contenedor con actividad propia además de la música y
    /// el temporizador. En «Aviso temporal» el plazo del aviso no recorta el
    /// recordatorio (001 RF-2): el aviso se repliega al compacto y aguanta ahí hasta que
    /// el evento empieza. En «Visible mientras activo» la actividad ya lo sostiene.
    /// </summary>
    private bool CalendarKeepsView() =>
        CalendarModeAvailable()
        && CalendarReminderLive()
        // En «Aviso temporal» el aviso vive su plazo y no se prolonga (001 RF-2): el
        // recordatorio es un aviso más. En «Visible mientras activo» lo sostiene su
        // propia actividad, igual que la música o el temporizador.
        && (SettingsManager.Current.IslandVisibilityMode == 0 || _noticeUntil > DateTime.UtcNow);

    /// <summary>
    /// Resumen de una línea del calendario para las pantallas combinadas
    /// (change island-pantallas RF-3): la cuenta atrás del evento que viene.
    /// </summary>
    internal IslandFeatureSummary CalendarSummary()
    {
        var next = _calendarReminder ?? GoogleCalendarService.Instance.Upcoming.FirstOrDefault();
        return new IslandFeatureSummary(Wpf.Ui.Controls.SymbolRegular.Calendar24,
            next?.CountdownText ?? "Sin eventos");
    }

    internal IslandFeatureState GetCalendarFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandCalendarEnabled;
        bool available = enabled && SettingsManager.Current.GoogleCalendarSignedIn;
        return new IslandFeatureState(enabled, available, Active: CalendarReminderLive(),
            Selected: _selectedFeature?.Id == IslandFeatureIds.Calendar, Exclusive: false);
    }

    internal bool ShowCalendarExpandedFromContract()
    {
        // La alerta final del temporizador es exclusiva: no se sustituye (002 RF-2).
        if (HasExclusive() || !CalendarModeAvailable()) return false;
        ExpandCalendar();
        return true;
    }

    internal bool ShowCalendarCompactFromContract()
    {
        if (!CalendarModeAvailable()) return false;
        ShowCalendarCompact();
        return true;
    }

    /// <summary>
    /// Vincula las vistas y se engancha al servicio: los avisos llegan desde su hilo de
    /// fondo, así que se cruzan al de UI antes de tocar nada. Se suelta al cerrar para
    /// que un contenedor cerrado no siga recibiendo recordatorios.
    /// </summary>
    private void InitCalendar()
    {
        RefreshCalendarList();
        GoogleCalendarService.Instance.ReminderDue += OnCalendarReminder;
        GoogleCalendarService.Instance.StateChanged += OnCalendarStateChanged;
        Closed += (_, _) =>
        {
            GoogleCalendarService.Instance.ReminderDue -= OnCalendarReminder;
            GoogleCalendarService.Instance.StateChanged -= OnCalendarStateChanged;
        };
        GoogleCalendarService.Instance.Configure();
    }

    /// <summary>
    /// Ajuste del calendario en caliente (sesión nueva, funcionalidad apagada, minutos
    /// cambiados): re-vincula listas, reconfigura el servicio y, si dejó de ser usable
    /// con su vista puesta, se repliega sin dejar una superficie vacía.
    /// </summary>
    public void RefreshCalendarContent() => Dispatcher.Invoke(() =>
    {
        RefreshCalendarList();
        GoogleCalendarService.Instance.Configure();
        if (CalendarModeAvailable())
        {
            if (IsBoxShown && _contentMode == IslandContentMode.Calendar)
            {
                ApplyContentVisibility();
                SyncMeasuredHeight();
            }
            UpdateArrows();
            return;
        }
        if (_contentMode == IslandContentMode.Calendar) FallbackFromCalendarView();
        UpdateArrows();
    });

    /// <summary>
    /// El calendario dejó de ser usable con su vista puesta (se cerró sesión o se apagó
    /// la funcionalidad): se repliega a la activa vigente o al reposo.
    /// </summary>
    private void FallbackFromCalendarView()
    {
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        _expanded = false;
        HidePerMode();
    }

    /// <summary>
    /// Pinta lo que publica el servicio: el próximo evento (o el recordatorio vigente)
    /// en el compacto y la lista completa en el expandido. Las cuentas atrás se
    /// recalculan al pintar, así que basta con repintar para que envejezcan bien.
    /// </summary>
    private void RefreshCalendarList()
    {
        var upcoming = GoogleCalendarService.Instance.Upcoming;
        CalendarExpandedList.ItemsSource = upcoming;
        CalendarExpandedEmpty.Visibility = upcoming.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        bool reminding = CalendarReminderLive();
        var next = reminding && _calendarReminder != null ? _calendarReminder : upcoming.FirstOrDefault();
        CalendarCompactTitle.Text = next?.Title ?? "Sin eventos próximos";
        CalendarCompactCountdown.Text = next?.CountdownText ?? "";
        CalendarCompactGlyph.Symbol = reminding ? SymbolRegular.Alert24 : SymbolRegular.Calendar24;
        CalendarExpandedHeader.Text = reminding ? "Empieza pronto" : "Próximos eventos";
        CalendarSyncedText.Text = GoogleCalendarService.Instance.LastSyncUtc == DateTime.MinValue
            ? "Sin sincronizar todavía."
            : $"Última sincronización: {GoogleCalendarService.Instance.LastSyncUtc.ToLocalTime():HH:mm}.";
    }

    private void ExpandCalendar() =>
        ShowExpandedView(IslandContentMode.Calendar, CalendarFeature, RefreshCalendarList);

    private void ShowCalendarCompact()
    {
        if (!CalendarModeAvailable()) { SnapHidden(); return; }
        RefreshCalendarList();
        ShowCompactView(IslandContentMode.Calendar, CalendarFeature, RefreshCalendarList);
    }

    /// <summary>
    /// Llegó un recordatorio: se fija el evento, se repinta y se despliega. Si la caja
    /// ya estaba expandida se expande el calendario (el aviso manda sobre lo que se
    /// estuviera mirando); si no, entra como compacto con su plazo de aviso. Con el
    /// contenedor apagado o suprimido (juego a pantalla completa) no se fuerza nada: al
    /// siguiente latido el estado de la funcionalidad ya cuenta el recordatorio.
    /// </summary>
    private void OnCalendarReminder(GoogleCalendarEvent item) => Dispatcher.Invoke(() =>
    {
        _calendarReminder = item;
        RefreshCalendarList();
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        // La alerta final del temporizador es exclusiva: no se pisa (002 RF-2). El
        // recordatorio queda anotado y se verá cuando esa exclusiva se cierre.
        if (HasExclusive()) return;
        if (_expanded) ExpandCalendar();
        else ShowCalendarCompact();
    });

    /// <summary>
    /// El servicio cambió lo publicable (eventos nuevos, error o fin de sesión): se
    /// repinta. Nunca altera la vista por sí solo —eso es del recordatorio—, así que
    /// sincronizar no interrumpe lo que el usuario esté mirando.
    /// </summary>
    private void OnCalendarStateChanged() => Dispatcher.Invoke(() =>
    {
        RefreshCalendarList();
        if (!IsBoxShown) return;
        if (_contentMode == IslandContentMode.Calendar) SyncMeasuredHeight();
    });
}
