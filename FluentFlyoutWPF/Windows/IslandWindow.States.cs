// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Máquina de estados del contenedor: qué vista se muestra (pieza inactiva,
/// compacto o expandido) y cómo se llega a ella.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item>El reposo lo decide el toggle «volver a inactivo» (pieza negra estrecha)
/// o nada (001 MOD RF-2).</item>
/// <item>«Visible mientras activo» nunca reposa con actividad vigente: el
/// compacto es de la activa del momento (001 MOD RF-4).</item>
/// <item>«Aviso temporal» muestra el contenido del evento durante el plazo
/// configurado, que no se reinicia al interactuar (001 RF-2, 002 RF-16).</item>
/// <item>El repliegue CON contenido pasa por la pieza inactiva en dos fases
/// (001 MOD RF-16); el repliegue hacia el reposo es una sola transición.</item>
/// </list>
///
/// <para>Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>,
/// el motor de animación en <c>IslandWindow.Frame.cs</c> y el contenido musical
/// en <c>IslandWindow.Media.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    private bool IsBoxShown => IslandBox.Visibility == Visibility.Visible;

    // --- aviso temporal (001 RF-2, 002 RF-16) ---

    /// <summary>
    /// Plazo del «Aviso temporal» (001 RF-2, 002 RF-8): el contenido mostrado por
    /// un evento se repliega al vencer la duración configurada. Vale para media
    /// Y para el temporizador. Nunca toca una exclusiva vigente (la alerta final
    /// se retira solo con su interacción).
    ///
    /// <para><paramref name="restart"/> = true cuando el aviso NACE (un evento
    /// nuevo): fija el vencimiento. Con false (repliegue de un aviso ya vigente,
    /// p. ej. al volver a compacto tras expandirlo) se conserva el vencimiento que
    /// ya tenía, así interactuar nunca prolonga el plazo (001 RF-2).</para>
    /// </summary>
    private void ArmTemporaryHide(bool restart = true)
    {
        if (SettingsManager.Current.IslandVisibilityMode != 1 || HasExclusive())
        {
            ClearTemporaryNotice();
            return;
        }
        if (restart || _noticeUntil == DateTime.MinValue)
        {
            int ms = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
            _noticeUntil = DateTime.UtcNow.AddMilliseconds(ms);
        }
        ScheduleNoticeRetraction();
    }

    /// <summary>
    /// El aviso se expandió: el plazo sigue corriendo, solo se pospone su repliegue
    /// hasta que la vista vuelva a ser compacta. Sin esto el vencimiento moría
    /// dentro del expandido y el aviso se quedaba pegado en compacto para siempre.
    /// </summary>
    private void HoldTemporaryNotice() => _noticeVersion++;

    /// <summary>Cierra el aviso vigente: no hay plazo pendiente que cumplir.</summary>
    private void ClearTemporaryNotice()
    {
        _noticeVersion++;
        _noticeUntil = DateTime.MinValue;
    }

    private void ScheduleNoticeRetraction()
    {
        int version = ++_noticeVersion;
        TimeSpan wait = _noticeUntil - DateTime.UtcNow;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        _ = Task.Delay(wait).ContinueWith(_ => Dispatcher.Invoke(() => RetractTemporaryNotice(version)));
    }

    /// <summary>
    /// Repliegue del aviso temporal al vencer su plazo. El puntero ni las
    /// actualizaciones de datos reinician el plazo, pero el aviso tampoco se
    /// cierra bajo el cursor ni mientras está expandido: se reintenta hasta que la
    /// vista vuelva a ser compacta y el ratón no estorbe. Así el aviso nunca se
    /// queda pegado indefinidamente (001 RF-2, 002 RF-8/RF-16).
    /// </summary>
    private void RetractTemporaryNotice(int version)
    {
        if (_disposed || version != _noticeVersion) return;
        if (SettingsManager.Current.IslandVisibilityMode != 1 || HasExclusive())
        {
            ClearTemporaryNotice();
            return;
        }
        if (!IsBoxShown)
        {
            ClearTemporaryNotice();
            return;
        }
        if (_expanded || IsMouseOverBoxOrStrip())
        {
            _ = Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => RetractTemporaryNotice(version)));
            return;
        }
        ClearTemporaryNotice();
        ShowInactiveOrHidden();
    }

    // --- resolución de la vista ---

    private void HidePerMode()
    {
        UpdateRotationPauseState();
        // Temporizador vivo: repliega a su compacto en vez de ocultar (H2 del spec 002).
        // Salvo avisando: la alerta manda y persiste expandida hasta X o reinicio.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        // «Visible mientras activo»: con algo activo SIEMPRE hay compacto; nunca se
        // oculta ni cae al reposo, y la vista se resuelve a la activa vigente con la
        // MISMA regla que el repliegue por puntero —así minimizar el temporizador
        // expandido con música sonando deja la música, no la pieza inactiva
        // (001 MOD RF-4, RF-24).
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } activa && activa.TryShowCompact())
            return;
        if (TimerKeepsAlive())
        {
            // T2: en Visible mientras activo un timer pausado es inactivo
            // (002 MOD RF-7) y no sostiene compacto: cae a inactivo/nada.
            if (SettingsManager.Current.IslandVisibilityMode == 0
                && _timer.State == IslandTimerState.Paused
                && !IsMediaActiveForContract())
            {
                // No hay activa vigente: caer a reposo sin sostener timer pausado.
            }
            else { ShowTimerCompact(); return; }
        }
        if (IsMouseOverBoxOrStrip())
        {
            // El puntero estorba: el aviso temporal no se cierra bajo el cursor,
            // pero tampoco se queda pegado — se le pone plazo si no hubiera uno
            // vigente (restart:false respeta el que ya corría), para que venza en
            // cuanto el ratón se aparte (001 RF-2, 002 RF-16).
            ArmTemporaryHide(restart: false);
            return;
        }
        if (_expanded)
        {
            _expanded = false;
            // Nada activo: el repliegue va DIRECTO a la pieza inactiva, en una sola
            // transición fluida —el contenido se desvanece mientras el ancho morfa
            // al de la pieza—: el compacto no es un paso intermedio que se vea
            // (001 MOD RF-4, RF-16).
            if (ReturnToInactive() || !AnimationsEnabled) { ShowInactiveOrHidden(); return; }
            _hidingViaCompact = true;
            _pT = 0; _qT = 0;
            EnsureLoop();
            return;
        }
        ShowInactiveOrHidden();
    }

    private bool IsTimerActiveForCompact() =>
        SettingsManager.Current.IslandEnabled
        && SettingsManager.Current.IslandTimerEnabled
        && _timer.State == IslandTimerState.Running;

    /// <summary>
    /// Funcionalidad activa VIGENTE en «Visible mientras activo» (001 MOD RF-4,
    /// RF-12). Actividad = contrato: media reproduciendo (o pausada si «pausa
    /// cuenta como activo») y timer en marcha; una pausa/timer pausado no
    /// sostiene el compacto. Con varias candidatas gana la del evento más
    /// reciente y, ante empate (mismo instante o sin evento registrado), la de
    /// mayor índice de registro: desempate explícito, sin depender del orden
    /// que devuelva un sort inestable.
    /// </summary>
    private IIslandFeature? ResolveActiveVigenteForVisible()
    {
        bool mediaActive = IsMediaActiveForContract();
        bool timerActive = IsTimerActiveForCompact();
        IIslandFeature? best = null;
        DateTime bestWhen = DateTime.MinValue;
        int bestIndex = -1;
        for (int i = 0; i < _features.Features.Count; i++)
        {
            var feature = _features.Features[i];
            bool active = feature.Id switch
            {
                "media" => mediaActive,
                "timer" => timerActive,
                _ => feature.State.Active, // funcionalidad futura: la declara ella
            };
            if (!active || !feature.State.Usable) continue;
            DateTime when = _lastFeatureEvent.TryGetValue(feature.Id, out var stamp) ? stamp : DateTime.MinValue;
            if (best == null || when > bestWhen || (when == bestWhen && i > bestIndex))
            {
                best = feature;
                bestWhen = when;
                bestIndex = i;
            }
        }
        return best;
    }

    /// <summary>¿Hay alguna funcionalidad habilitada y disponible? (001 MOD RF-9)</summary>
    private bool AnyFeatureUsable() => _features.UsableFeatures().Any();

    /// <summary>
    /// El toggle «volver a inactivo» decide el reposo: pieza negra visible o
    /// nada (001 MOD RF-2, por defecto inactivo visible). Sin funcionalidades
    /// usables no hay pieza: no se ancla una caja vacía.
    /// </summary>
    private bool ReturnToInactive() =>
        SettingsManager.Current.IslandReturnToInactive && AnyFeatureUsable();

    /// <summary>
    /// Reposo del contenedor (001 MOD RF-2): pieza inactiva o nada según el
    /// toggle; ante supresión, oculto (la pieza también se suprime, RF-14).
    ///
    /// <para>Es el ÚNICO punto por el que se entra al reposo, así que también
    /// garantiza su coherencia (001 MOD RF-4, RF-16): en «Visible mientras activo»
    /// con actividad vigente el reposo NUNCA es la pieza —se muestra el compacto
    /// de la activa— y por tanto no existe el paso pieza → compacto que se veía
    /// al minimizar con algo activo.</para>
    /// </summary>
    private void ShowInactiveOrHidden()
    {
        if (Suppressed() && !HasExclusive()) { SnapHidden(); return; }
        if (!ReturnToInactive()) { GoHidden(); return; }
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } activa && activa.TryShowCompact())
            return;
        ShowInactive();
    }

    /// <summary>
    /// Estado inactivo (001 MOD RF-11, RF-16): pill negra más estrecha que el
    /// compacto, sin ninguna vista de contenido; el hover solo la agranda y el
    /// clic abre la última usable.
    /// </summary>
    private void ShowInactive()
    {
        // Diagnóstico del contrato de actividad: la pieza no debería reposar con
        // media reproduciendo en «Visible mientras activo» (001 MOD RF-4). Si
        // aparece en el log, la resolución de actividad dejó pasar una sesión.
        if (SettingsManager.Current.IslandVisibilityMode == 0 && !_expanded
            && _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            Logger.Warn("Island: reposo en la pieza con media reproduciendo " +
                "(snapshot={Snap}, permitidas reproduciendo={Playing}); revisar la resolución de actividad",
                _music?.Id, NewestPlaying() != null);
        ClearTemporaryNotice();
        // El repliegue parte de la geometría expandida: el compacto no debe
        // florecer de paso mientras el ancho morfa al de la pieza (001 MOD RF-16).
        _collapseFromExpanded = _expanded || _p > 0.02;
        _hidingViaCompact = false;
        _expanded = false;
        _contentMode = 0;
        _inactiveHot = false;
        SetInactiveRest(true);
        // Animado cuando la pieza todavía no domina la vista (contenido que fundir)
        // o cuando la geometría aún no es la de reposo (expandido que morfar): en
        // ambos casos hay algo que interpolar.
        if (AnimationsEnabled && IsBoxShown && (_inactiveT < 1 || _p > 0.05))
        {
            // Transición animada: el contenido actual (la vista compacta que
            // había) se queda en el árbol y se desvanece MIENTRAS el ancho
            // morfa al de la pieza; la limpieza real corre al asentarse
            // (FinishInactive), nunca antes (001 MOD RF-16).
            _pT = 0;
            _qT = 1;
            PositionTopCenter();
            IslandBox.Visibility = Visibility.Visible;
            UpdateRotationPauseState();
            UpdateLine();
            EnsureLoop();
            return;
        }
        // Reposo inmediato (animaciones off o caja recién aparecida): sin
        // contenido que fundir, la pieza se limpia de golpe.
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _inactiveHotT = 0;
        ClearInactiveResidue();
        PositionTopCenter();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    /// <summary>
    /// Fija el objetivo del reposo inactivo. Hacia la pieza (on) funde la vista
    /// actual hacia ella; hacia contenido delega en <see cref="EnterContent"/>.
    /// Con la caja oculta (o las animaciones apagadas) el progreso salta directo:
    /// no hay contenido que fundir ni morfología que interpolar, y arrancaría un
    /// ancho equivocado.
    /// </summary>
    private void SetInactiveRest(bool on)
    {
        if (!on) { EnterContent(); return; }
        // OJO: la reapertura pendiente (_pendingCompactFeature) NO se toca aquí. La
        // pieza es un punto de tránsito: al llegar a ella, TryReopenFromInactive
        // decide si se reabre el compacto (actividad vigente o fase 2 de un
        // repliegue en dos fases) o si el reposo se queda (001 MOD RF-16).
        _inactiveTt = 1;
        _inactiveShown = true;
        if (!AnimationsEnabled || !IsBoxShown)
        {
            _inactiveT = 1;
            return;
        }
        EnsureLoop();
    }

    /// <summary>
    /// Punto ÚNICO de entrada al contenido (compacto o expandido, 001 MOD RF-16):
    /// mientras hay una funcionalidad a la vista, el reposo inactivo no aplica ni
    /// deja residuos. Si la pieza ya domina la vista (reposo real), su progreso se
    /// funde con el reloj de reposo —crossfade pieza &lt;-&gt; contenido—; si solo era un
    /// residuo a medio camino (repliegue interrumpido), se descarta de golpe, de modo
    /// que el residuo accidental nunca se ve. El repliegue DELIBERADO hacia el
    /// compacto sí atraviesa la pieza: lo hace en dos fases encadenadas por
    /// <see cref="BeginCollapseThroughInactive"/> (001 MOD RF-16).
    /// </summary>
    private void EnterContent()
    {
        // Entrar a contenido cancela cualquier repliegue pendiente: la vista es
        // contenido, no hay fase 2 que encadenar ni compacto que reabrir después.
        _pendingCompactFeature = null;
        _collapseFromExpanded = false;
        _inactiveShown = false;
        _inactiveTt = 0;
        if (_inactiveT <= 0) return;
        if (_inactiveT >= 1 && AnimationsEnabled && IsBoxShown)
        {
            EnsureLoop(); // la pieza es la vista actual: fundido elegante hacia el contenido
            return;
        }
        // Residuo en camino al reposo: el contenido manda, sin pasar por la pieza.
        _inactiveT = 0;
        if (AnimationsEnabled && IsBoxShown) EnsureLoop();
    }

    /// <summary>
    /// La pieza inactiva es la vista ASENTADA del contenedor: ya no hay geometría
    /// en vuelo, ni fase 2 pendiente, ni expansión a la vista. Es la única
    /// situación en la que el contenedor puede reabrir el compacto sin romper un
    /// repliegue deliberado a mitad de camino (001 MOD RF-16).
    /// </summary>
    private bool AtInactiveRest =>
        !_disposed && _inactiveShown && !_expanded && !_hidingViaCompact
        && _inactiveT >= 1 && _inactiveTt == 1 && _pT == 0 && _p <= 0.02;

    /// <summary>
    /// ¿La funcionalidad sigue sosteniendo la vista AHORA? Media lo hace
    /// reproduciendo (o pausada si «pausa cuenta como activo»: 001 MOD RF-6/RF-7) y
    /// el temporizador contando —en «Aviso temporal» también con su alerta vigente
    /// (002 MOD RF-8)—. El cajón de aplicaciones no tiene actividad propia: solo
    /// lo sostiene el plazo de su aviso. La funcionalidad futura lo declara ella.
    /// </summary>
    private bool FeatureSustainsView(IIslandFeature feature) => feature.Id switch
    {
        "media" => IsMediaActiveForContract(),
        "timer" => SettingsManager.Current.IslandVisibilityMode == 0
            ? IsTimerActiveForCompact()
            : TimerKeepsAlive(),
        // El cajón no tiene actividad propia: en «Visible mientras activo» la
        // vista la sostiene el puntero; en «Aviso temporal», su plazo (001 RF-2).
        "apps" => AppsKeepsView(),
        _ => feature.State.Active,
    };

    /// <summary>
    /// Salida del reposo inactivo: ÚNICO punto por el que la pieza se reabre.
    ///
    /// <list type="number">
    /// <item>Fase 2 de un repliegue en dos fases (<c>_pendingCompactFeature</c>):
    /// la funcionalidad que estaba expandida vuelve en compacto.</item>
    /// <item>Llegada de actividad sin evento propio en «Visible mientras activo»:
    /// el reposo nunca tapa lo que está activo (001 MOD RF-4, RF-24).</item>
    /// </list>
    ///
    /// En «Aviso temporal» el contenido solo vive lo que vive su aviso (001 RF-2,
    /// 002 RF-16): sin plazo vigente la pieza se queda. Devuelve true si la vista
    /// ya está entrando (el reloj de reposo se funde hacia el contenido).
    ///
    /// <para>La reapertura usa el reloj RÁPIDO de salida del reposo
    /// (<see cref="InactiveReopenSeconds"/>): salir de la pieza dura menos que
    /// entrar en ella, porque el usuario que vuelve mira, no espera.</para>
    /// </summary>
    private bool TryReopenFromInactive()
    {
        var target = _pendingCompactFeature;
        _pendingCompactFeature = null;
        if (_disposed || !SettingsManager.Current.IslandEnabled || Suppressed()) return false;
        if (HasExclusive() || _timer.State == Classes.IslandTimerState.Alerting) return false;
        if (SettingsManager.Current.IslandVisibilityMode == 1 && _noticeUntil <= DateTime.UtcNow)
            return false;
        // El repliegue no puede revivir contenido que dejó de estar activo a mitad
        // de vuelo (pausa que no cuenta como activa, temporizador cancelado, sesión
        // cerrada): en ese caso la reapertura se descarta (001 MOD RF-4/RF-7).
        if (target != null && !FeatureSustainsView(target)) target = null;
        if (target == null)
        {
            if (SettingsManager.Current.IslandVisibilityMode != 0) return false;
            target = ResolveActiveVigenteForVisible();
        }
        if (target == null || !target.State.Usable) return false;
        // La reapertura NO es un evento nuevo: el aviso conserva el plazo que le
        // quedaba en lugar de reiniciarse —interactuar nunca prolonga el aviso
        // (001 RF-2)—. La entrada a contenido arranca aquí, con el mismo reloj de
        // reposo: crossfade pieza -> contenido, sin saltos ni estados intermedios.
        DateTime notice = _noticeUntil;
        if (!target.TryShowCompact()) return false;
        _noticeUntil = notice;
        if (notice != DateTime.MinValue) ScheduleNoticeRetraction();
        return true;
    }

    /// <summary>
    /// Cierre de la transición a inactivo: ahora sí se retira el contenido
    /// residual (001 MOD RF-11) y se apagan indicadores. Se llama solo cuando
    /// el progreso llegó a 1, jamás a mitad de vuelo.
    /// </summary>
    private void FinishInactive()
    {
        // La pieza es la vista final de este repliegue: la próxima reapertura
        // entra por la ruta normal (y así el contenido puede florecer).
        _collapseFromExpanded = false;
        ClearInactiveResidue();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    /// <summary>
    /// Limpieza real del estado inactivo (001 MOD RF-11): no basta con fundir
    /// las capas por opacidad, el contenido residual (grillas compactas con la
    /// última funcionalidad, carátula, fondo, títulos y textos del temporizador)
    /// se retira de verdad. La restauración corre por las rutas normales
    /// (ApplyContentVisibility + RefreshUi/RefreshTimerUI al mostrar
    /// compacto o expandido).
    /// </summary>
    private void ClearInactiveResidue()
    {
        // Grillas de contenido compacto: fuera del árbol visual mientras dura el reposo.
        MusicCompactGrid.Visibility = Visibility.Collapsed;
        TimerCompactGrid.Visibility = Visibility.Collapsed;
        AppsCompactGrid.Visibility = Visibility.Collapsed;
        // Datos musicales: sin carátula, fondo difuminado, títulos ni seek.
        ClearMusicResidue();
        // Datos del temporizador: sin restante ni progreso heredados.
        TimerRemaining.Text = "00:00:00";
        TimerProgressFill.Width = 0;
        TimerRunRemaining.Text = "00:00:00";
        // Por si algún panel expandido quedó visible de la vista anterior.
        TimerAlert.Visibility = Visibility.Collapsed;
        AppsExpanded.Visibility = Visibility.Collapsed;
        ApplyFrame();
    }

    // --- transiciones instantáneas (sin animaciones) ---

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        if (Math.Abs(_p) > 0.05)
        {
            _expanded = false; _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return;
        }
        _qT = 0;
        EnsureLoop();
    }

    private void SnapCompact()
    {
        _inactiveShown = false;
        _pendingCompactFeature = null;
        _collapseFromExpanded = false;
        _inactiveTt = 0; _inactiveT = 0;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }

    private void SnapHidden()
    {
        _hidingViaCompact = false;
        _inactiveShown = false;
        _pendingCompactFeature = null;
        _collapseFromExpanded = false;
        ClearTemporaryNotice();
        _inactiveTt = 0; _inactiveT = 0;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 0; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Collapsed;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    // --- repliegue del puntero ---

    // Repliegue a compacto conservando el contenido (último-activo, 001 MOD RF-4).
    // El compacto se alcanza PASANDO por la pieza inactiva (fase 1 → fase 2): el
    // usuario pidió que el repliegue con contenido pase primero por el cuadrado
    // negro estrecho y recién después florezca el compacto (001 MOD RF-16).
    private void CollapseToCompact(IIslandFeature target)
    {
        _expanded = false;
        _hidingViaCompact = false;
        UpdateLine();
        PositionTopCenter();
        if (!AnimationsEnabled)
        {
            EnterContent();
            _p = _pT = 0; _pv = 0;
            ApplyFrame();
            return;
        }
        // Animado, el repliegue con contenido pasa PRIMERO por la pieza inactiva
        // (el cuadrado negro) y solo después florece el compacto con su contenido
        // (001 MOD RF-16). El aviso temporal vigente conserva su plazo.
        if (BeginCollapseThroughInactive(target))
        {
            ArmTemporaryHide(restart: false);
            return;
        }
        // Ya en el compacto: sin geometría que replegar, solo se cancela el reposo.
        EnterContent();
        _pT = 0;
        EnsureLoop();
    }

    /// <summary>
    /// El puntero se alejó del island expandido (001 MOD RF-4): resuelve a qué
    /// vista se repliega. En «Aviso temporal» conserva la misma funcionalidad que
    /// estaba expandida (002 RF-8) cuando sigue sosteniendo la vista y si no manda
    /// la activa vigente (con música sonando el compacto es la música, nunca la
    /// pieza: 001 MOD RF-4); en «Visible mientras activo» manda la activa vigente
    /// por último evento. Sin activa vigente el repliegue va al reposo.
    /// </summary>
    private void LeaveHover()
    {
        if (!_expanded) return;
        // Alerta de fin (exclusiva): persistente hasta X o reinicio, aunque el
        // ratón se vaya; el minimizado ordinario no la toca (001 MOD RF-4).
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        if (HasExclusive()) return;
        if (Suppressed()) { HidePerMode(); return; }

        // Delta island-compacto-activo-animado: en Visible mientras activo el
        // compacto al minimizar resuelve a la activa vigente por último evento
        // (pausa-OFF = inactiva, timer pausado = inactivo); en Aviso temporal
        // conserva la misma funcionalidad que estaba expandida (001 MOD RF-4/RF-24, 002 MOD RF-7/RF-8).
        if (SettingsManager.Current.IslandVisibilityMode == 1)
        {
            // El aviso temporal solo conserva contenido mientras su plazo siga
            // vivo: si ya venció (o no hay aviso), replegar al compacto sería un
            // destello condenado y el repliegue va DIRECTO al reposo (001 RF-2,
            // 001 MOD RF-16). Interactuar nunca prolonga el plazo.
            bool noticeAlive = _noticeUntil > DateTime.UtcNow;
            if (_contentMode == 0)
            {
                // La vista musical vigente, adoptando sesión si el snapshot no la
                // tenía: sin esto el aviso temporal caía al reposo con música
                // sonando y el compacto aparecía un tick después (RF-11, RF-13).
                // Con «pausa cuenta como activo» desactivado una pausa no sostiene
                // el compacto: la vista cae a inactivo/nada (001 MOD RF-4, RF-7).
                var session = IsMediaActiveForContract() ? ActiveMediaSession() : null;
                if (session != null && noticeAlive && MediaFeature is { } media) { CollapseToCompact(media); return; }
                ShowInactiveOrHidden();
                return;
            }
            if (_contentMode == 1)
            {
                // Conserva la funcionalidad expandida (002 RF-8); si el temporizador
                // ya no sostiene la vista, manda la activa vigente: con música
                // sonando el compacto es la música, nunca la pieza (001 MOD RF-4).
                if (TimerKeepsAlive() && noticeAlive) { ShowTimerCompact(); return; }
                if (noticeAlive && _timer.State != Classes.IslandTimerState.Alerting
                    && ResolveActiveVigenteForVisible() is { } vigente1 && vigente1.TryShowCompact())
                    return;
                ShowInactiveOrHidden();
                return;
            }
            if (_contentMode == AppsContentMode)
            {
                // El cajón es un aviso más: se repliega al compacto mientras su
                // plazo siga vivo y, si ya venció, al reposo sin destellos
                // (001 RF-2, 001 MOD RF-16).
                if (AppsKeepsView()) { ShowAppsCompact(); return; }
                ShowInactiveOrHidden();
                return;
            }
            HidePerMode();
            return;
        }

        // Visible mientras activo: resolver a activo vigente por último evento.
        var vigente = ResolveActiveVigenteForVisible();
        if (vigente == null)
        {
            ShowInactiveOrHidden();
            return;
        }
        if (vigente.Id == "media")
        {
            // La actividad manda sobre el snapshot: si el snapshot apunta a una
            // pausa mientras otra sesión reproduce, el compacto muestra lo activo.
            var session = ActiveMediaSession();
            if (session != null)
            {
                // El compacto debe ser media activa vigente sin residuos: volver
                // a compacto limpio del contenido expandido previo si era timer.
                if (_contentMode == 1)
                {
                    _contentMode = 0;
                    ApplyContentVisibility();
                }
                // Si ya estábamos en media, basta colapsar conservando contenido:
                // la vista entra por el repliegue en dos fases (pieza → compacto).
                if (vigente.TryShowCompact()) return;
                CollapseToCompact(vigente);
                return;
            }
            ShowInactiveOrHidden();
            return;
        }
        if (vigente.Id == "timer")
        {
            if (vigente.TryShowCompact()) return;
            ShowInactiveOrHidden();
            return;
        }
        // Fallback genérico (futura funcionalidad): usar su compacto.
        if (vigente.TryShowCompact()) return;
        ShowInactiveOrHidden();
    }

    /// <summary>
    /// Expande la última usable con repliegue seguro: si la elegida deja de ser
    /// presentable en el último instante, prueba la siguiente usable; si no hay
    /// ninguna, resuelve el reposo sin abrir una caja vacía (RF-3, RF-9, RF-16).
    /// </summary>
    private bool ExpandLastUsable()
    {
        if ((Suppressed() && !HasExclusive()) || !SettingsManager.Current.IslandEnabled) { SnapHidden(); return false; }
        var feature = LastUsableFeature();
        if (feature != null && feature.TryShowExpanded()) return true;
        var alt = _features.NextUsableAfter(feature);
        if (alt != null && alt.TryShowExpanded()) return true;
        HidePerMode();
        return false;
    }
}
