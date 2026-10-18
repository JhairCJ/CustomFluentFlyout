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
    private IReadOnlyList<GoogleCalendarEvent> _publishedUpcoming = Array.Empty<GoogleCalendarEvent>();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private int _generation;
    private string _configuredAccount = "", _configuredClient = "";
    private readonly HashSet<string> _reminded = [];

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private DateTime _lastFetch = DateTime.MinValue;

    /// <summary>Copia de los próximos eventos, ya ordenados por comienzo.</summary>
    public IReadOnlyList<GoogleCalendarEvent> Upcoming
    {
        get { lock (_gate) return _publishedUpcoming; }
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
        var settings = SettingsManager.Current;
        if (wanted && running && (_configuredAccount != settings.GoogleCalendarAccount || _configuredClient != settings.GoogleCalendarClientId))
        {
            Stop();
            running = false;
        }
        _configuredAccount = settings.GoogleCalendarAccount;
        _configuredClient = settings.GoogleCalendarClientId;
        if (wanted && !running) Start();
        else if (!wanted && (running || Syncing)) Stop();
    }

    /// <summary>Sincroniza ahora, sin esperar al siguiente ciclo (botón de ajustes).</summary>
    public async Task RefreshNowAsync()
    {
        if (!Wanted()) return;
        int generation = _generation;
        var expectedSettings = SettingsManager.Current;
        try
        {
            await FetchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Calendario: sincronización manual fallida");
            if (generation == _generation && Wanted() && ReferenceEquals(expectedSettings, SettingsManager.Current)) Fail(ex);
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    private static bool Wanted()
    {
        var settings = SettingsManager.Current;
        return settings.IslandCalendarEnabled && settings.GoogleCalendarSignedIn;
    }

    private void Start()
    {
        var cancellation = new CancellationTokenSource();
        _loopCts = cancellation;
        _loop = Task.Run(() => LoopAsync(cancellation.Token));
    }

    private void Stop()
    {
        ++_generation;
        var previous = _loopCts;
        var previousLoop = _loop;
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { /* ya cerrado */ }
        _loopCts = null;
        _loop = null;
        if (previous != null)
        {
            if (previousLoop != null) _ = previousLoop.ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
            else previous.Dispose();
        }
        lock (_gate)
        {
            _upcoming.Clear();
            _publishedUpcoming = Array.Empty<GoogleCalendarEvent>();
            _reminded.Clear();
        }
        LastSyncUtc = DateTime.MinValue;
        _lastFetch = DateTime.MinValue;
        NotifyStateChanged();
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
            int generation = _generation;
            var expectedSettings = SettingsManager.Current;
            try
            {
                var settings = SettingsManager.Current;
                if (settings.IslandCalendarEnabled && settings.GoogleCalendarSignedIn)
                {
                    int everyMinutes = Math.Clamp(settings.GoogleCalendarRefreshMinutes, 1, 60);
                    if ((DateTime.UtcNow - _lastFetch).TotalMinutes >= everyMinutes) await FetchAsync(ct);
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
                if (generation == _generation && Wanted() && ReferenceEquals(expectedSettings, SettingsManager.Current)) Fail(ex);
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        int generation = _generation;
        await _fetchGate.WaitAsync(ct);
        var settings = SettingsManager.Current;
        string account = settings.GoogleCalendarAccount;
        if (!Wanted() || generation != _generation) { _fetchGate.Release(); return; }
        Syncing = true;
        NotifyStateChanged();
        try
        {
            string token = await GoogleCalendarClient.EnsureAccessTokenAsync(ct);
            // Un minuto de margen atrás para no perder el evento que acaba de empezar.
            DateTime from = DateTime.Now.AddMinutes(-1);
            DateTime to = DateTime.Now.AddDays(Math.Clamp(settings.GoogleCalendarDaysAhead, 1, 14));
            var events = await GoogleCalendarClient.FetchUpcomingAsync(token, from, to, 20, ct);
            lock (_gate)
            {
                if (generation != _generation || !Wanted() || !ReferenceEquals(settings, SettingsManager.Current) || account != settings.GoogleCalendarAccount) return;
                _upcoming.Clear();
                _upcoming.AddRange(events);
                _publishedUpcoming = Array.AsReadOnly(_upcoming.ToArray());
                _reminded.IntersectWith(_upcoming.Select(item => $"{item.Id}@{item.Start.Ticks}"));
                LastSyncUtc = DateTime.UtcNow;
                _lastFetch = DateTime.UtcNow;
            }
            settings.GoogleCalendarError = "";
        }
        finally
        {
            Syncing = false;
            _fetchGate.Release();
            NotifyStateChanged();
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

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Logger.Debug(ex, "Calendar state subscriber failed"); }
    }

    private static void Fail(Exception ex) =>
        SettingsManager.Current.GoogleCalendarError = ex.Message switch
        {
            { Length: > 0 } text => text,
            _ => IslandStrings.Get("IslandCalendarReadFailed", "Could not read the calendar"),
        };
}
