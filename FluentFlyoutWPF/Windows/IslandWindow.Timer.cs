// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Temporizador del Fluent Island (spec 002): contenido del contenedor junto a
/// música y al cajón de aplicaciones. Compacto con icono + restante + progreso
/// opcional; expandido con tiempo libre + controles a la izquierda y presets a
/// la derecha; aviso a cero con despliegue forzado; navegación entre
/// funcionalidades con rueda y flechas.
/// </summary>
public partial class IslandWindow
{
    private readonly IslandTimer _timer = new();
    private IslandContentMode _contentMode; // banda de contenido vigente (IslandWindow.Views.cs)
    private bool _pendingTimerAlert;
    private TimeSpan _staged = TimeSpan.Zero; // valor de los reels, origen personalizado
    private bool _timerInputCustom = true;
    // Anti-reaparición tras descartar: el ratón sigue encima y el poll re-expandiría.
    private const int TimerReshowSnoozeSeconds = 2;
    // Arrastre de reel: píxeles por unidad y estado del gesto.
    private const double ReelPixelsPerUnit = 24;
    private bool _reelDragging;
    private string _reelDragUnit = "";
    private double _reelDragStartY;
    private int _reelDragStartVal;

    private bool TimerModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandTimerEnabled;

    // Implementaciones del contrato del contenedor para el temporizador
    // (001 MOD RF-11, RF-13): cada vista se abre solo si sigue siendo usable.
    internal bool ShowTimerExpandedFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ExpandTimer();
        return true;
    }

    internal bool ShowTimerCompactFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ShowTimerCompact();
        return true;
    }

    private bool TimerKeepsAlive() =>
        TimerModeAvailable() && (_timer.IsCounting || _timer.State == IslandTimerState.Alerting || _pendingTimerAlert);

    /// <summary>
    /// ¿El temporizador tiene un aviso que mostrar? Vigente = plazo del aviso
    /// temporal corriendo; PENDIENTE = una acción de la cuenta (empezar,
    /// reanudar, reiniciar) hecha dentro del expandido, cuyo plazo arranca
    /// cuando el compacto se presenta (002 RF-16). Sin esto, replegarse desde el
    /// expandido iba directo al reposo y el aviso del temporizador no se veía
    /// nunca en «Aviso temporal».
    /// </summary>
    private bool TimerNoticeAlive() => _noticeUntil > DateTime.UtcNow || _pendingTimerNotice;

    /// <summary>
    /// «Aviso temporal» (001 RF-2, 002 RF-16): las acciones que ponen la cuenta
    /// en marcha —empezar, reanudar, reiniciar— generan el aviso del
    /// temporizador igual que reproducir genera el de media. El plazo NO corre
    /// dentro del expandido: queda pendiente y arranca cuando el compacto del
    /// temporizador se presenta (<see cref="ShowTimerCompact"/>), que es cuando
    /// el aviso se ve, de modo que el usuario disfruta la duración configurada
    /// completa. La cuenta no se toca y la alerta final (exclusiva) no admite
    /// aviso.
    /// </summary>
    private void ArmTimerNotice()
    {
        if (SettingsManager.Current.IslandVisibilityMode != 1 || !TimerModeAvailable()) return;
        if (HasExclusive()) { ClearTemporaryNotice(); return; }
        // Con el compacto del temporizador ya a la vista manda su plazo vigente:
        // la acción de la cuenta no reinicia un aviso que ya estaba corriendo
        // (001 RF-2). Vale también con el temporizador dentro de una pantalla.
        if (!_expanded && IsBoxShown && ViewShowsFeature(IslandFeatureIds.Timer)) return;
        _pendingTimerNotice = true;
        if (!_expanded) ShowTimerCompact();
    }

    private void InitTimer()
    {
        _timer.Finished += OnTimerFinished;
        // Notificación de cambio de estado (002 MOD RF-2/RF-13): el host rearma su
        // despertador único de vencimiento y publica la actividad; la cuenta NO
        // depende de ningún latido.
        _timer.Changed += OnTimerChanged;
        TimerPresetList.ItemsSource = SettingsManager.Current.IslandTimerPresets;
    }

    /// <summary>
    /// Ajuste de habilitación: al apagar con cuenta activa se cancela (RF-10).
    /// </summary>
    public void RefreshTimerEnabled()
    {
        if (!TimerModeAvailable())
        {
            _timer.Cancel();
            _pendingTimerAlert = false;
            if (_contentMode == IslandContentMode.Timer)
            {
                _contentMode = IslandContentMode.Media;
                if (_expanded)
                {
                    // Solo vuelve la música si el contenido musical está
                    // habilitado y el snapshot la tiene disponible.
                    var session = MusicContentShown() ? Current() : null;
                    if (session != null) RefreshUi(session);
                    else { ClearMusicResidue(); HidePerMode(); }
                }
                else HidePerMode();
            }
        }
        RefreshTimerUI();
        ApplyContentVisibility();
    }

    private void RefreshTimerUI()
    {
        UpdateArrows();
        if (!TimerModeAvailable()) return;
        TimerRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        TimerProgressZone.Visibility = SettingsManager.Current.IslandTimerShowProgress
            ? Visibility.Visible : Visibility.Collapsed;
        double track = TimerProgressTrack.ActualWidth;
        TimerProgressFill.Width = track > 0 ? track * _timer.ElapsedFraction : 0;
        bool idle = _timer.State == IslandTimerState.Idle;
        if (idle) PaintReels();
        TimerRunOrigin.Text = _timer.OriginLabel;
        TimerRunRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        TimerRunPauseGlyph.Symbol = _timer.State == IslandTimerState.Paused
            ? Wpf.Ui.Controls.SymbolRegular.Play24
            : Wpf.Ui.Controls.SymbolRegular.Pause24;
        TimerStartBtn.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        TimerRestartBtn.Visibility = idle && _timer.Configured > TimeSpan.Zero
            ? Visibility.Visible : Visibility.Collapsed;
        TimerAlertName.Text = _timer.OriginLabel;
    }

    private void PaintReels()
    {
        long total = (long)_staged.TotalSeconds;
        ReelH.Text = $"{total / 3600:00}";
        ReelM.Text = $"{(total % 3600) / 60:00}";
        ReelS.Text = $"{total % 60:00}";
    }

    /// <summary>
    /// Conmuta las capas de contenido y, con el mismo cambio, ajusta las
    /// cadencias por contenido y el ecualizador: el refresco de vista y el
    /// visualizador solo corren con su contenido en pantalla (001 MOD RF-14/16;
    /// 002 MOD RF-15).
    /// </summary>
    private void ApplyContentVisibility()
    {
        ApplyContentVisibilityCore();
        SyncEq();
        UpdateVisibleRefresh();
    }

    /// <summary>
    /// Conmuta las capas de contenido del contenedor (música, temporizador,
    /// cajón, estante o calendario) en compacto y expandido. Cada capa se muestra
    /// solo si su funcionalidad sigue siendo usable: sin disponibilidad no hay
    /// vista vacía (001 MOD RF-9).
    /// </summary>
    private void ApplyContentVisibilityCore()
    {
        // La capa compacta recupera su ancho de siempre: el compacto nunca agrupa
        // (change island-pantallas), así que mide lo que una sola funcionalidad.
        CompactLayer.Width = SingleCompactLayerWidth;
        if (_contentMode == IslandContentMode.Screen)
        {
            // El modo PANTALLA es la composición del EXPANDIDO (sus columnas, de
            // izquierda a derecha): es lo que abre el clic. Replegada —o con una sola
            // funcionalidad usable, o sin columnas que enseñar— la pantalla no compone
            // nada y vuelve a la vista rica de la funcionalidad que manda (RF-1), que en
            // el compacto es UNA sola. Sin ninguna usable no hay nada que componer.
            if (!_expanded || !ScreenIsCombined())
            {
                if (CurrentScreenFeatures().Count > 0
                    && (_expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact())) return;
                ShowInactiveOrHidden();
                return;
            }
            ApplyScreenLayerVisibility();
            return;
        }
        // Fuera de una pantalla combinada los paneles del expandido viven en su sitio
        // de siempre (no-op si no se había movido ninguno a una columna).
        RestoreExpandedHomes();
        WeatherCompactGrid.Visibility = Visibility.Collapsed;
        WeatherExpanded.Visibility = Visibility.Collapsed;
        PowerCompactGrid.Visibility = Visibility.Collapsed;
        // Cargador: rayo y porcentaje, un aviso temporal excluyente con el resto de
        // capas (como el de Bluetooth, que no tiene expandido).
        bool power = _contentMode == IslandContentMode.Power && PowerModeAvailable();
        PowerCompactGrid.Visibility = power ? Visibility.Visible : Visibility.Collapsed;
        if (power)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            ShelfCompactGrid.Visibility = Visibility.Collapsed;
            CalendarCompactGrid.Visibility = Visibility.Collapsed;
            BluetoothCompactGrid.Visibility = Visibility.Collapsed;
            ClipboardCompactGrid.Visibility = Visibility.Collapsed;
            WeatherCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            ShelfExpanded.Visibility = Visibility.Collapsed;
            CalendarExpanded.Visibility = Visibility.Collapsed;
            ClipboardExpanded.Visibility = Visibility.Collapsed;
            WeatherExpanded.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        // Clima: su vista (glifo y temperatura en compacto, lugar y extremos en el
        // expandido) es excluyente con el resto de capas, como el portapapeles.
        bool weather = _contentMode == IslandContentMode.Weather && WeatherModeAvailable();
        PowerCompactGrid.Visibility = Visibility.Collapsed;
        WeatherCompactGrid.Visibility = weather ? Visibility.Visible : Visibility.Collapsed;
        WeatherExpanded.Visibility = weather ? Visibility.Visible : Visibility.Collapsed;
        if (weather)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            ShelfCompactGrid.Visibility = Visibility.Collapsed;
            CalendarCompactGrid.Visibility = Visibility.Collapsed;
            BluetoothCompactGrid.Visibility = Visibility.Collapsed;
            ClipboardCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            ShelfExpanded.Visibility = Visibility.Collapsed;
            CalendarExpanded.Visibility = Visibility.Collapsed;
            ClipboardExpanded.Visibility = Visibility.Collapsed;
            WeatherExpanded.Visibility = Visibility.Visible;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        // Portapapeles: fila de piezas copiadas (o lista en el expandido), excluyente
        // con el resto de capas, como el cajón y el estante.
        bool clipboard = _contentMode == IslandContentMode.Clipboard && ClipboardModeAvailable();
        ClipboardCompactGrid.Visibility = clipboard ? Visibility.Visible : Visibility.Collapsed;
        ClipboardExpanded.Visibility = clipboard ? Visibility.Visible : Visibility.Collapsed;
        if (clipboard)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            ShelfCompactGrid.Visibility = Visibility.Collapsed;
            CalendarCompactGrid.Visibility = Visibility.Collapsed;
            BluetoothCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            ShelfExpanded.Visibility = Visibility.Collapsed;
            CalendarExpanded.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        // Bluetooth: su vista es un aviso temporal (icono, nombre y batería) y es
        // excluyente con el resto de capas, como el estante y el calendario. Se
        // resuelve de una vez al principio, así ninguna otra rama puede dejar su
        // capa visible por venir de la vista anterior.
        bool bluetooth = _contentMode == IslandContentMode.Bluetooth && BluetoothModeAvailable();
        BluetoothCompactGrid.Visibility = bluetooth ? Visibility.Visible : Visibility.Collapsed;
        if (bluetooth)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            ShelfCompactGrid.Visibility = Visibility.Collapsed;
            CalendarCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            ShelfExpanded.Visibility = Visibility.Collapsed;
            CalendarExpanded.Visibility = Visibility.Collapsed;
            ClipboardExpanded.Visibility = Visibility.Collapsed;
            WeatherExpanded.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        // Estante primero: es la única capa que puede estar visible sin elementos
        // (vacía invita a soltar), así que se resuelve sola y no comparte la lógica de
        // música/temporizador.
        bool shelf = _contentMode == IslandContentMode.Shelf && ShelfModeAvailable();
        ShelfCompactGrid.Visibility = shelf ? Visibility.Visible : Visibility.Collapsed;
        ShelfExpanded.Visibility = shelf ? Visibility.Visible : Visibility.Collapsed;
        if (shelf)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        // Calendario: vista propia con actividad (recordatorio vivo), igual de
        // excluyente con el resto de capas que el estante.
        bool calendar = _contentMode == IslandContentMode.Calendar && CalendarModeAvailable();
        CalendarCompactGrid.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        CalendarExpanded.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        if (calendar)
        {
            MusicCompactGrid.Visibility = Visibility.Collapsed;
            TimerCompactGrid.Visibility = Visibility.Collapsed;
            AppsCompactGrid.Visibility = Visibility.Collapsed;
            ShelfCompactGrid.Visibility = Visibility.Collapsed;
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            AppsExpanded.Visibility = Visibility.Collapsed;
            ShelfExpanded.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        bool apps = _contentMode == IslandContentMode.Apps && AppsModeAvailable();
        bool timer = _contentMode == IslandContentMode.Timer && TimerModeAvailable();
        AppsCompactGrid.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        AppsExpanded.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        if (apps) MusicCompactGrid.Visibility = TimerCompactGrid.Visibility = Visibility.Collapsed;
        if (apps)
        {
            MusicExpandedTop.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            SeekRow.Visibility = Visibility.Collapsed;
            TimerAlert.Visibility = Visibility.Collapsed;
            CrossfadeTimerPanels(showConfig: false, showRun: false);
            UpdateArrows();
            return;
        }
        MusicCompactGrid.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        TimerCompactGrid.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        bool alert = timer && _timer.State == IslandTimerState.Alerting;
        bool idle = timer && _timer.State == IslandTimerState.Idle;
        MusicExpandedTop.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        ControlsRow.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        if (timer)
        {
            SeekRow.Visibility = Visibility.Collapsed;
        }
        else if (MusicContentShown() && Current() is { } session)
        {
            ApplyCapabilities(session); // restaura SeekRow según la fuente
        }
        else
        {
            // Sin sesión que presentar: la fila de seek no se deja visible
            // (no hay vista musical vacía, 001 MOD RF-9).
            SeekRow.Visibility = Visibility.Collapsed;
        }
        // Los paneles config/progreso los conmuta el fundido; la alerta es instantánea.
        TimerAlert.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
        CrossfadeTimerPanels(showConfig: timer && idle && !alert, showRun: timer && !idle && !alert);
        UpdateArrows();
    }

    // Solo fundido de entrada: el saliente colapsa instantáneo para no medir
    // dos paneles apilados (eso inflaba la altura).
    private void CrossfadeTimerPanels(bool showConfig, bool showRun)
    {
        FadeInPanel(TimerExpanded, showConfig);
        FadeInPanel(TimerRunPanel, showRun);
    }

    // Fundido SOLO en la transición oculto->visible: la opacidad LOCAL del
    // panel es siempre 1 (el valor final), y la animación solo la conduce
    // durante los 150 ms del fundido. Así, repetir ApplyContentVisibility
    // (actualizaciones del contenedor, settings, start/pause) nunca puede
    // revertir el panel a opacidad 0 y dejar la caja negra (001/002 ADDED RF-1).
    private static void FadeInPanel(UIElement el, bool show)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1; // valor local = estado final; la animación solo cubre el gesto
        if (!show)
        {
            el.Visibility = Visibility.Collapsed;
            return;
        }
        if (el.Visibility == Visibility.Visible) return;
        el.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        fadeIn.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    // Todo cambio de estado del motor re-conmuta paneles + re-mide la altura.
    // El loop lo garantiza SyncMeasuredHeight si el objetivo cambió.
    private void RefreshTimerModeView()
    {
        // Una acción que deja la cuenta en marcha (empezar, reanudar, reiniciar)
        // genera el aviso del temporizador en «Aviso temporal» (002 RF-16): es el
        // único punto por el que el motor comunica su estado nuevo, así que el
        // aviso se arma aquí y no en cada botón.
        if (_timer.State == IslandTimerState.Running) ArmTimerNotice();
        // T2: si el timer acaba de pausarse y en «Visible mientras activo» no hay
        // otra activa vigente, la vista debe caer a inactivo/nada —el compacto
        // pausado no sostiene nada (002 MOD RF-7)—. Vale tanto si el panel estaba
        // expandido como si el compacto del timer seguía a la vista: una sola
        // regla, un solo punto de salida.
        if (_timer.State == IslandTimerState.Paused
            && SettingsManager.Current.IslandVisibilityMode == 0
            && !IsMediaActiveForContract()
            && (_expanded || (IsBoxShown && ViewShowsFeature(IslandFeatureIds.Timer))))
        {
            _expanded = false;
            ShowInactiveOrHidden();
            return;
        }
        ApplyContentVisibility();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    /// <summary>
    /// Red de seguridad del 001/002 ADDED RF-1: mientras el temporizador es el
    /// contenido activo y visible, su panel (compacto o expandido) tiene que
    /// estar presente. Si una actualización del contenedor dejara la caja sin
    /// ningún panel de timer (superficie negra), se re-aplica el contenido y
    /// se registra el incidente para diagnosticarlo.
    /// </summary>
    private void EnsureTimerContentShown()
    {
        if (_disposed || _contentMode != IslandContentMode.Timer || !IsBoxShown || !TimerModeAvailable()) return;
        if (_timer.State == IslandTimerState.Alerting
            && TimerAlert.Visibility == Visibility.Visible) return;
        bool compactOk = TimerCompactGrid.Visibility == Visibility.Visible;
        bool expandedOk = TimerExpanded.Visibility == Visibility.Visible
            || TimerRunPanel.Visibility == Visibility.Visible
            || TimerAlert.Visibility == Visibility.Visible;
        if (_expanded ? expandedOk : compactOk) return;
        Logger.Warn("Island: contenido del temporizador ausente con caja visible " +
            "(expanded={Expanded}, timerState={State}, compact={Compact}, " +
            "config={Config}, run={Run}, alert={Alert}); re-aplicando contenido",
            _expanded, _timer.State,
            TimerCompactGrid.Visibility, TimerExpanded.Visibility,
            TimerRunPanel.Visibility, TimerAlert.Visibility);
        ApplyContentVisibility();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    private void UpdateArrows()
    {
        // Flechas opcionales: solo con más de una PANTALLA con algo usable, sean
        // cuales sean sus funcionalidades (002 MOD RF-9; change island-pantallas
        // RF-4: la navegación es por pantallas).
        bool show = _expanded && SettingsManager.Current.IslandTimerShowArrows && IsBoxShown
            && UsableScreenCount() > 1;
        ModePrevBtn.Visibility = ModeNextBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTimerCompact()
    {
        if (!TimerModeAvailable()) { SnapHidden(); return; }
        // «Aviso temporal»: el compacto del timer es un aviso como el de media y
        // también vence (002 RF-8/RF-16) con la cuenta intacta por detrás.
        // Un aviso PENDIENTE (acción de la cuenta dentro del expandido) estrena
        // aquí su plazo: se descarta el vencimiento heredado —o ya gastado en el
        // expandido— y ShowCompactView lo arma con la duración configurada, así
        // el aviso se ve entero en vez de nacer vencido (001 RF-2, 002 RF-16).
        if (_pendingTimerNotice)
        {
            _pendingTimerNotice = false;
            _noticeUntil = DateTime.MinValue;
        }
        ShowCompactView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI);
    }

    /// <summary>
    /// Cierre de la cuenta por acción del usuario (X del aviso final o cancelar
    /// desde el panel de marcha): el Island NO se queda abierto en la
    /// configuración, se contrae como cualquier otra contracción
    /// (001 MOD RF-4, 002 RF-6). El veto por puntero encima no aplica aquí —el
    /// usuario acaba de terminar el temporizador a propósito— y se silencia la
    /// reapertura por hover para que la caja no vuelva sola a los 150 ms.
    /// </summary>
    private void CompactAfterTimerStopped()
    {
        _staged = TimeSpan.Zero;
        TimerStatus.Text = "";
        _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        _expanded = false;
        RefreshTimerUI();
        if (SettingsManager.Current.IslandVisibilityMode == 0)
        {
            // Visible mientras activo: manda la activa vigente (pausa-OFF no
            // sostiene nada y cae a inactivo/nada: 001 MOD RF-4).
            if (ResolveActiveVigenteForVisible()?.TryShowCompact() == true) return;
        }
        else if (ActiveMediaSession() is { } session)
        {
            // Aviso temporal: con sesión la vista vuelve a media (002 RF-6) y ese
            // aviso vuelve a cumplir su propio plazo.
            ShowMusicCompact(session);
            return;
        }
        // Sin activa vigente ni sesión que presentar: reposo sin residuos.
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        ClearMusicResidue();
        ShowInactiveOrHidden();
    }

    private void ExpandTimer()
    {
        if (!TimerModeAvailable()) return;
        ShowExpandedView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI);
    }

    private void OnTimerFinished() => Dispatcher.Invoke(ShowTimerAlert);

    /// <summary>
    /// Aviso a cero: fuerza el despliegue aunque estuviera oculto; con acceso
    /// exclusivo persistente atraviesa supresión (001 RF-8/14, 002 RF-2).
    /// </summary>
    private void ShowTimerAlert()
    {
        if (!TimerModeAvailable()) return;
        if (!SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = true; return; }
        // skipIfExpanded:false — la alerta es exclusiva y debe imponerse sobre la
        // vista vigente aunque la caja ya estuviera expandida (002 RF-2/RF-6).
        ShowExpandedView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI, skipIfExpanded: false);
    }

    /// <summary>
    /// Cambia de PANTALLA (rueda o flechas, 002 MOD RF-3/RF-9): recorre las pantallas
    /// con algo usable en el orden de la lista de pantallas, con vuelta, en el sentido
    /// indicado. Con el expandido delante cada pantalla abre su composición de columnas
    /// —o la vista rica de su única funcionalidad— y, con el compacto, la vista rica de
    /// la funcionalidad que la sostiene (el compacto nunca agrupa), así que sin
    /// disponibilidad no se aterriza en una vista vacía (001 MOD RF-9).
    ///
    /// <para>Nunca se toca el motor de cuenta ni el snapshot: cambiar de vista no
    /// cancela ni reinicia la cuenta del temporizador (002 MOD RF-9, RF-13).</para>
    /// </summary>
    private void CycleMode(int direction = 1)
    {
        // Alerta modal: hasta X o reinicio no se sale al resto de modos.
        if (_timer.State == IslandTimerState.Alerting) return;
        // Navegación por PANTALLAS (change island-pantallas RF-4): cada paso busca
        // la siguiente pantalla con algo usable, con vuelta, y la presenta en el
        // estado en el que esté el contenedor (sus columnas si está expandido, la
        // vista rica de una de sus funcionalidades si está en compacto).
        if (_screens.Count == 0) return;
        for (int step = 1; step <= _screens.Count; step++)
        {
            int index = WrapUnit(_screenIndex + direction * step, _screens.Count);
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            bool shown = _expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact();
            if (!shown) HidePerMode();
            return;
        }
    }

    // --- controles del expandido ---

    private void TimerCompact_Click(object sender, MouseButtonEventArgs e) => ExpandTimer();

    private void ModeArrow_Click(object sender, RoutedEventArgs e) =>
        CycleMode(ReferenceEquals(sender, ModePrevBtn) ? -1 : 1);

    private static int WrapUnit(int value, int count) => ((value % count) + count) % count;

    private void SplitStaged(out int h, out int m, out int s)
    {
        long total = (long)_staged.TotalSeconds;
        h = (int)(total / 3600);
        m = (int)((total % 3600) / 60);
        s = (int)(total % 60);
    }

    private void ReelStep(string unit, int dir)
    {
        SplitStaged(out int h, out int m, out int s);
        switch (unit)
        {
            case "H": h = WrapUnit(h + dir, 25); break;
            case "M": m = WrapUnit(m + dir, 60); break;
            default: s = WrapUnit(s + dir, 60); break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        RefreshTimerUI();
    }

    private void Reel_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string unit) return;
        _reelDragging = true;
        _reelDragUnit = unit;
        _reelDragStartY = e.GetPosition(this).Y;
        SplitStaged(out int h, out int m, out int s);
        _reelDragStartVal = unit switch { "H" => h, "M" => m, _ => s };
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Reel_Move(object sender, MouseEventArgs e)
    {
        if (!_reelDragging || sender is not FrameworkElement el || !el.IsMouseCaptured) return;
        // Arriba aumenta, abajo reduce; el valor envuelve (reel infinito).
        int steps = (int)((_reelDragStartY - e.GetPosition(this).Y) / ReelPixelsPerUnit);
        SplitStaged(out int h, out int m, out int s);
        int current = _reelDragUnit switch { "H" => h, "M" => m, _ => s };
        int count = _reelDragUnit == "H" ? 25 : 60;
        int next = WrapUnit(_reelDragStartVal + steps, count);
        if (next == current) return;
        switch (_reelDragUnit)
        {
            case "H": h = next; break;
            case "M": m = next; break;
            default: s = next; break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        PaintReels();
    }

    private void Reel_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
        _reelDragging = false;
        RefreshTimerUI();
    }

    // Rueda sobre un dígito: arriba reduce, abajo aumenta (criterio del dueño).
    private void Reel_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.Tag is not string unit) return;
        ReelStep(unit, e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void TimerPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimerPresetList.SelectedItem is TimerPreset preset)
        {
            _timerInputCustom = false;
            _staged = TimeSpan.FromSeconds(preset.DurationSeconds);
            TimerStatus.Text = "";
            RefreshTimerUI();
        }
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        TimeSpan duration;
        string origin;
        if (!_timerInputCustom && TimerPresetList.SelectedItem is TimerPreset preset)
        {
            duration = TimeSpan.FromSeconds(preset.DurationSeconds);
            origin = preset.Name;
        }
        else
        {
            duration = _staged;
            origin = "Timer";
        }
        if (!_timer.Start(duration, origin))
            TimerStatus.Text = "Duración no válida: usa 00:00:01 a 24:00:00.";
        else
        {
            NoteFeatureEvent("timer");
            TimerStatus.Text = "";
        }
        RefreshTimerModeView();
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.State == IslandTimerState.Running) _timer.Pause();
        else if (_timer.State == IslandTimerState.Paused) { _timer.Resume(); NoteFeatureEvent("timer"); }
        RefreshTimerModeView();
    }

    private void TimerRestart_Click(object sender, RoutedEventArgs e)
    {
        _timer.Restart();
        _staged = _timer.Configured;
        TimerStatus.Text = "";
        RefreshTimerModeView();
    }

    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancelar es un cierre deliberado: el Island se contrae, no se queda
        // abierto en la configuración (001 MOD RF-4).
        _timer.Cancel();
        CompactAfterTimerStopped();
    }

    private void TimerAlertRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.Configured > TimeSpan.Zero)
            _timer.Start(_timer.Configured, _timer.OriginLabel);
        RefreshTimerModeView();
    }

    private void TimerAlertDismiss_Click(object sender, RoutedEventArgs e)
    {
        // X del aviso final (002 RF-6): cancela la cuenta y contrae el Island
        // (a media si hay sesión, si no a inactivo/nada según toggle), sin
        // quedarse mostrando el aviso descolgado (001 MOD RF-4).
        _timer.Cancel();
        SelectFeature("media");
        CompactAfterTimerStopped();
    }
}
