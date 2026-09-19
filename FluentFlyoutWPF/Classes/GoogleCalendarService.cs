// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Recordatorios de Google Calendar: lee los próximos eventos del calendario principal
/// y avisa cuando uno está a punto de empezar.
///
/// <para>Un único bucle en segundo plano (15 s) hace dos cosas: resincronizar la lista
/// cada <c>GoogleCalendarRefreshMinutes</c> y comprobar la ventana de recordatorio
/// (de <c>GoogleCalendarReminderMinutes</c> antes del comienzo hasta 2 minutos
/// después). Cada evento avisa UNA vez por comienzo: si se reprograma, vuelve a
/// avisar.</para>
///
/// <para>El servicio no conoce el Island: levanta <see cref="ReminderDue"/> y
/// <see cref="StateChanged"/>, y quien los escuche decide qué pintar. Todo el estado
/// compartido va bajo un cerrojo porque el bucle vive fuera del hilo de UI.</para>
/// </summary>
public sealed class GoogleCalendarService
{
    /// <summary>Instancia única: la app tiene un solo calendario que recordar.</summary>
    public static GoogleCalendarService Instance { get; } = new();

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>Cuánto se espera entre comprobaciones de la ventana de recordatorio.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>Minutos de gracia tras el comienzo en los que el aviso sigue vivo.</summary>
    private const int GraceMinutes = 2;

    private readonly Lock _gate = new();
    private readonly List<GoogleCalendarEvent> _upcoming = [];
    private readonly HashSet<string> _reminded = [];

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private DateTime _lastFetch = DateTime.MinValue;

    /// <summary>Copia de los próximos eventos, ya ordenados por comienzo.</summary>
    public IReadOnlyList<GoogleCalendarEvent> Upcoming
    {
        get { lock (_gate) return [.. _upcoming]; }
    }

    /// <summary>Instante de la última sincronización correcta (MinValue = ninguna).</summary>
    public DateTime LastSyncUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Hay una sincronización en curso.</summary>
    public bool Syncing { get; private set; }

    /// <summary>Un evento entra en su ventana de recordatorio (una vez por comienzo).</summary>
    public event Action<GoogleCalendarEvent>? ReminderDue;

    /// <summary>Cambió lo que se puede pintar: eventos nuevos, error o fin de sesión.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Arranca o para el bucle según los ajustes: con la funcionalidad apagada o sin
    /// sesión de Google no hay nada que leer, y dejar el bucle vivo sería gastar red y
    /// token para nada. Se llama al arrancar y en cada cambio de ajustes.
    /// </summary>
    public void Configure()
    {
        bool wanted = Wanted();
        bool running = _loop != null;
        if (wanted && !running) Start();
        else if (!wanted && running) Stop();
    }

    /// <summary>Sincroniza ahora, sin esperar al siguiente ciclo (botón de ajustes).</summary>
    public async Task RefreshNowAsync()
    {
        if (!Wanted()) return;
        try
        {
            await FetchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Calendario: sincronización manual fallida");
            Fail(ex);
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    private static bool Wanted()
    {
        var settings = SettingsManager.Current;
        return settings.IslandCalendarEnabled && settings.GoogleCalendarSignedIn;
    }

    private void Start()
    {
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private void Stop()
    {
        try { _loopCts?.Cancel(); }
        catch (ObjectDisposedException) { /* ya cerrado */ }
        _loopCts = null;
        _loop = null;
        lock (_gate)
        {
            _upcoming.Clear();
            _reminded.Clear();
        }
        LastSyncUtc = DateTime.MinValue;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Bucle de fondo: resincroniza cuando toca y comprueba recordatorios en cada
    /// vuelta. Los fallos no lo matan —se anotan en los ajustes para que se vean en
    /// la página y el siguiente ciclo reintenta—; solo la cancelación lo termina.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = SettingsManager.Current;
                if (settings.IslandCalendarEnabled && settings.GoogleCalendarSignedIn)
                {
                    int everyMinutes = Math.Clamp(settings.GoogleCalendarRefreshMinutes, 1, 60);
                    if ((DateTime.Now - _lastFetch).TotalMinutes >= everyMinutes) await FetchAsync(ct);
                    CheckReminders();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Calendario: ciclo de recordatorios");
                Fail(ex);
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        var settings = SettingsManager.Current;
        if (!settings.GoogleCalendarSignedIn) return;
        Syncing = true;
        try
        {
            string token = await GoogleCalendarClient.EnsureAccessTokenAsync(ct);
            // Un minuto de margen atrás para no perder el evento que acaba de empezar.
            DateTime from = DateTime.Now.AddMinutes(-1);
            DateTime to = DateTime.Now.AddDays(Math.Clamp(settings.GoogleCalendarDaysAhead, 1, 14));
            var events = await GoogleCalendarClient.FetchUpcomingAsync(token, from, to, 20, ct);
            lock (_gate)
            {
                _upcoming.Clear();
                _upcoming.AddRange(events);
            }
            LastSyncUtc = DateTime.UtcNow;
            settings.GoogleCalendarError = "";
            _lastFetch = DateTime.Now;
        }
        finally
        {
            Syncing = false;
        }
    }

    /// <summary>
    /// Avisa de los eventos que acaban de entrar en su ventana. El registro de
    /// «ya avisados» es por evento y comienzo, así que reprogramar un evento vuelve a
    /// avisar, pero el mismo comienzo no avisa dos veces.
    /// </summary>
    private void CheckReminders()
    {
        int lead = Math.Clamp(SettingsManager.Current.GoogleCalendarReminderMinutes, 1, 60);
        DateTime now = DateTime.Now;
        List<GoogleCalendarEvent> due = [];
        lock (_gate)
        {
            foreach (var item in _upcoming)
            {
                if (item.AllDay) continue;
                if (now < item.Start.AddMinutes(-lead) || now > item.Start.AddMinutes(GraceMinutes)) continue;
                if (_reminded.Add($"{item.Id}@{item.Start.Ticks}")) due.Add(item);
            }
        }
        foreach (var item in due) ReminderDue?.Invoke(item);
    }

    private static void Fail(Exception ex) =>
        SettingsManager.Current.GoogleCalendarError = ex.Message switch
        {
            { Length: > 0 } text => text,
            _ => "No se pudo leer el calendario.",
        };
}
