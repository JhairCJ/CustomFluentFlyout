// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media.Control;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Máquina de estados del contenedor: qué vista se muestra (nada, pieza inactiva,
/// compacto o expandido) y cómo se llega a ella.
///
/// <para>Reglas que sostiene (change island-lista-de-activos):</para>
/// <list type="bullet">
/// <item><b>Una sola decisión</b>: la vista sale SIEMPRE de la LISTA DE EVENTOS ACTIVOS
/// (<c>IslandActivityRegistry</c>) traducida por la política pura
/// (<c>IslandPresentation</c>). Cada ruta de cada funcionalidad publica su evento y llama
/// a <see cref="RefreshPresentation"/>; ninguna decide por su cuenta qué se ve. Antes esa
/// misma decisión estaba escrita en seis sitios con guardas distintas —de ahí los
/// compactos que tardaban y los que no aparecían nunca.</item>
/// <item><b>Instante</b>: un evento activo se presenta en el MISMO turno. La geometría
/// acompaña después (muelles), pero nada espera a que se asiente: la pieza inactiva es un
/// DESTINO (el reposo), no un paso obligatorio del repliegue.</item>
/// <item><b>El reposo lo decide el ajuste</b>: pieza negra estrecha o nada (001 MOD RF-2),
/// y solo con alguna pantalla usable: nunca una caja vacía.</item>
/// <item><b>El aviso temporal conserva su plazo</b>: interactuar no lo prolonga (001 RF-2)
/// y re-presentar el mismo aviso tampoco (su instante de nacimiento manda).</item>
/// </list>
///
/// <para>Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>, el motor
/// de animación en <c>IslandWindow.Frame.cs</c> y el contenido en las fichas
/// (<c>IslandWindow.FeatureCards.cs</c>).</para>
/// </summary>
public partial class IslandWindow
{
    private bool IsBoxShown => IslandBox.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------
    // Lista de eventos activos (change island-lista-de-activos)
    // ------------------------------------------------------------------

    /// <summary>Lista de lo que está pasando ahora mismo: actividad viva, avisos con plazo y exclusivas.</summary>
    private readonly IslandActivityRegistry _activity = new();

    /// <summary>Vista aplicada en el último <see cref="ApplyPresentation"/>: es lo que permite reparar.</summary>
    private IslandPresentationResult? _appliedPresentation;

    /// <summary>Acción vacía para las fichas sin repintado propio.</summary>
    private static readonly Action NoOp = static () => { };

    /// <summary>
    /// Funcionalidades con actividad PROPIA: viven mientras pasa algo (reproducir,
    /// contar, un recordatorio). Las que solo llevan AVISOS —Bluetooth, cargador,
    /// dictado— y las de contenido bajo demanda —cajón, estante, portapapeles, clima— no
    /// están aquí: su vista vive lo que vive el plazo que arma su presentación.
    /// </summary>
    private static readonly string[] LiveActivityFeatures =
        [IslandFeatureIds.Media, IslandFeatureIds.Timer, IslandFeatureIds.Calendar];

    /// <summary>
    /// Vuelca el estado de las funcionalidades a la lista de eventos activos. Se llama en
    /// cada resolución: repetir el mismo estado no toca las entradas —su instante de
    /// nacimiento, que es el desempate, sobrevive a los refrescos—.
    ///
    /// <para>En «Aviso temporal» NO hay actividad viva: allí el contenido vive lo que vive
    /// su aviso, y ese aviso lo arma la presentación del compacto (001 RF-2).</para>
    /// </summary>
    private void PublishActivity()
    {
        var now = DateTime.UtcNow;
        bool temporalMode = SettingsManager.Current.IslandVisibilityMode == 1;
        var inScreens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (feature, _) in ScreenOrderedFeatures())
        {
            inScreens.Add(feature.Id);
            if (!LiveActivityFeatures.Contains(feature.Id)) continue;
            _activity.SetLive(feature.Id, FeatureIsLiveActivity(feature, temporalMode), now);
        }
        // Una funcionalidad que se quedó sin ninguna pantalla no tiene vista: su actividad
        // deja de contar (antes se quedaba en la lista y ya no la miraba nadie).
        foreach (string id in LiveActivityFeatures)
        {
            if (!inScreens.Contains(id)) _activity.SetLive(id, false, now);
        }
        // Acceso exclusivo: la alerta del temporizador y el dictado en marcha, que la
        // política hace valer por encima de cualquier otra actividad.
        foreach (var feature in _features.Features)
            _activity.SetExclusive(feature.Id, feature.State.Exclusive, now);
    }

    /// <summary>
    /// ¿Esta funcionalidad es ACTIVIDAD VIVA con el modo dado? En «Visible mientras activo»
    /// lo es mientras esté pasando (reproducir, contar, un recordatorio). En «Aviso temporal»
    /// el contenido vive lo que vive su plazo, con UNA excepción que es un ESTADO y no un
    /// aviso: una sesión PAUSADA que el usuario ha declarado activa sostiene la vista hasta
    /// que reanude o se cierre la sesión.
    ///
    /// <para>Sin esto el ajuste no servía de nada en «Aviso temporal»: pausar mostraba el
    /// compacto durante el plazo configurado y luego lo escondía por completo, cuando lo que
    /// el usuario pidió con «pausa cuenta como activo» es que la pausa SEA la vista
    /// (change island-lista-de-activos).</para>
    /// </summary>
    private bool FeatureIsLiveActivity(IIslandFeature feature, bool temporalMode)
    {
        if (!temporalMode) return FeatureIsActiveNow(feature);
        return feature.Id switch
        {
            // Música pausada: es el ajuste el que decide si la pausa es estado.
            IslandFeatureIds.Media => SettingsManager.Current.IslandPauseCountsActive && MediaPausedNow(),
            // Cuenta pausada: sigue siendo una cuenta, con su tiempo restante a la vista.
            IslandFeatureIds.Timer => _timer.State == IslandTimerState.Paused,
            _ => false,
        };
    }

    /// <summary>
    /// ¿Esta funcionalidad está pasando AHORA? La actividad propia la declara su ficha
    /// (reproducir, contar); una funcionalidad sin actividad propia la declara su
    /// sostenimiento, y una futura sin ficha, su contrato.
    /// </summary>
    private bool FeatureIsActiveNow(IIslandFeature feature) =>
        FeatureCard(feature.Id) switch
        {
            { OwnActivity: { } own } => own(),
            { } card => card.Sustains(),
            _ => feature.State.Active,
        };

    /// <summary>
    /// Funcionalidades con vista posible, en ORDEN DE PANTALLAS y con las que conservan su
    /// vista propia fuera de ellas al final (avisos y exclusivas: su tarjeta no depende de
    /// que el usuario las haya colocado en ninguna parte).
    /// </summary>
    private List<IIslandFeature> PresentationFeatures()
    {
        var list = new List<IIslandFeature>();
        foreach (var (feature, _) in ScreenOrderedFeatures()) list.Add(feature);
        foreach (var feature in _features.Features)
        {
            if (!ScreenlessFeatureKeepsOwnView(feature)) continue;
            if (list.Any(f => f.Id == feature.Id)) continue;
            list.Add(feature);
        }
        return list;
    }

    /// <summary>Funcionalidad que sostiene la vista vigente (null con una pantalla combinada o sin vista).</summary>
    private string? CurrentViewFeatureId() => _contentMode == IslandContentMode.Screen
        ? CompactMemberOfCurrentScreen()?.Id
        : ViewOwnerFeature()?.Id;

    /// <summary>Funcionalidad del aviso presentado, si su plazo sigue vivo.</summary>
    private string? PresentedNoticeId(DateTime now) =>
        _noticeUntil > now ? _noticeFeatureId : null;

    /// <summary>Snapshot completo para la política: es lo único que ella consulta.</summary>
    private IslandPresentationInput PresentationInput(bool userExpanded)
    {
        var now = DateTime.UtcNow;
        var features = PresentationFeatures();
        var ordered = new List<string>(features.Count);
        var usable = new List<string>(features.Count);
        foreach (var feature in features)
        {
            ordered.Add(feature.Id);
            if (feature.State.Usable) usable.Add(feature.Id);
        }
        // Con expandido propio: solo las que están en una PANTALLA. Un aviso o una exclusiva
        // fuera de ellas (el dictado) vive en el compacto.
        var expandable = new List<string>(features.Count);
        foreach (var (feature, _) in ScreenOrderedFeatures()) expandable.Add(feature.Id);
        return new IslandPresentationInput(
            Suppressed: Suppressed(),
            ReturnToInactive: SettingsManager.Current.IslandReturnToInactive,
            UserExpanded: userExpanded,
            UserExpandedFeatureId: CurrentViewFeatureId(),
            AnyScreenUsable: AnyScreenUsable(),
            PresentedNoticeId: PresentedNoticeId(now),
            PresentedNoticeStarted: _noticeStarted,
            ScreenOrderedIds: ordered,
            ExpandableIds: expandable,
            UsableIds: usable,
            Activity: _activity,
            Now: now);
    }

    /// <summary>Vista que toca mostrar AHORA según la lista de activos (pura, sin tocar nada).</summary>
    private IslandPresentationResult DesiredPresentation() =>
        IslandPresentation.Resolve(PresentationInput(userExpanded: _expanded));

    // ------------------------------------------------------------------
    // Aplicación de la vista (punto ÚNICO)
    // ------------------------------------------------------------------

    /// <summary>
    /// Publica la actividad y aplica la vista que le toca. Es el ÚNICO punto por el que el
    /// contenedor entra a una vista: lo llaman las rutas de cada funcionalidad, los
    /// ajustes, el vencimiento del aviso, el puntero y el arranque.
    /// </summary>
    private void RefreshPresentation()
    {
        if (_disposed) return;
        PublishActivity();
        ApplyPresentation(DesiredPresentation());
    }

    /// <summary>
    /// Repara la vista si la lista de activos cambió mientras la geometría estaba en
    /// vuelo: se re-resuelve y, si es otra, se aplica. Es la red que garantiza que el
    /// contenedor JAMÁS se quede en la pieza teniendo algo activo (change
    /// island-lista-de-activos).
    /// </summary>
    private void RepairPresentation()
    {
        if (_disposed || !SettingsManager.Current.IslandEnabled) return;
        PublishActivity();
        var desired = DesiredPresentation();
        if (_appliedPresentation is { } applied
            && applied.View == desired.View && applied.FeatureId == desired.FeatureId)
            return;
        ApplyPresentation(desired);
    }

    /// <summary>
    /// Pinta la vista deseada. La CARA (la tarjeta de la funcionalidad con su contenido) se
    /// presenta siempre y en el mismo turno; la animación solo decide la geometría. Con el
    /// modo «Aviso temporal» y sin aviso vivo se cae al reposo (001 RF-2).
    /// </summary>
    private void ApplyPresentation(IslandPresentationResult desired)
    {
        // Idempotencia: la vista que YA está aplicada no se vuelve a presentar (evita
        // repintar la pieza y reiniciar su micro-animación en cada pasada del buzón). El
        // compacto se deja pasar: ahí puede tocar armar el plazo de un aviso nuevo.
        if (desired.View != IslandDesiredView.Compact
            && _appliedPresentation is { } applied
            && applied.View == desired.View && applied.FeatureId == desired.FeatureId)
            return;
        _appliedPresentation = desired;
        if (_disposed) return;
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        switch (desired.View)
        {
            case IslandDesiredView.Hidden:
                _expanded = false;
                // Ya oculto: no hay nada que replegar (y no se toca el aviso vigente, que
                // puede estar esperando a su propio evento).
                if (!IsBoxShown && _qT == 0) return;
                GoHidden();
                return;
            case IslandDesiredView.Inactive:
                _expanded = false;
                ShowInactive();
                return;
            case IslandDesiredView.Expanded:
                PresentExpanded(desired);
                return;
            default:
                PresentCompact(desired);
                return;
        }
    }

    /// <summary>
    /// Presenta el COMPACTO de la funcionalidad activa. Si su cara ya está delante, solo se
    /// repinta su contenido y —si el aviso es nuevo— se arma su plazo: repetir la misma
    /// vista no reanima nada.
    /// </summary>
    private void PresentCompact(IslandPresentationResult desired)
    {
        var feature = desired.FeatureId == null ? null : FeatureById(desired.FeatureId);
        if (feature == null || !feature.State.Usable)
        {
            // La activa dejó de ser presentable en el último instante: reposo, nunca una
            // caja vacía (001 MOD RF-9).
            PresentRest();
            return;
        }
        _expanded = false;
        var mode = ModeForFeature(feature.Id);
        // El aviso presentado era de otra funcionalidad: se olvida con su vista.
        if (_noticeFeatureId != null && _noticeFeatureId != feature.Id) ClearTemporaryNotice();
        if (IsBoxShown && !_inactiveShown && _inactiveTt == 0 && _contentMode == mode)
        {
            FeatureCard(feature.Id)?.Refresh();
            ApplyContentVisibility();
            ArmTemporaryHide(restart: desired.RestartNotice, force: desired.AlwaysTemporal);
            UpdateLine();
            // La geometría también tiene que llegar al compacto: si el usuario tenía el
            // expandido abierto, esta es la ÚNICA transición (expandido → compacto, sin
            // pasar por la pieza), y la cara del compacto ya está presentada.
            if (AnimationsEnabled) SetCompactFrame();
            return;
        }
        ShowCompactView(mode, feature, FeatureCard(feature.Id)?.Refresh ?? NoOp,
            forceNotice: desired.AlwaysTemporal, restartNotice: desired.RestartNotice);
    }

    /// <summary>
    /// Presenta el EXPANDIDO de la funcionalidad que lo sostiene (la exclusiva vigente o el
    /// que el usuario tiene abierto). Con su expandido ya delante solo se repinta: la
    /// alerta final se fuerza para imponerse sobre cualquier vista (002 RF-2/RF-6).
    /// </summary>
    private void PresentExpanded(IslandPresentationResult desired)
    {
        var feature = desired.FeatureId == null ? null : FeatureById(desired.FeatureId);
        if (feature == null)
        {
            // Sin dueño identificable (una pantalla sin miembro concreto): se repinta la
            // vista vigente tal cual.
            if (IsBoxShown) { ApplyContentVisibility(); SyncMeasuredHeight(); }
            return;
        }
        bool alert = feature.Id == IslandFeatureIds.Timer && _timer.State == IslandTimerState.Alerting;
        var mode = ModeForFeature(feature.Id);
        if (_expanded && _contentMode == mode && !alert)
        {
            FeatureCard(feature.Id)?.Refresh();
            ApplyContentVisibility();
            SyncMeasuredHeight();
            _pT = 1;
            _qT = 1;
            if (AnimationsEnabled) EnsureLoop();
            return;
        }
        ShowExpandedView(mode, feature, FeatureCard(feature.Id)?.Refresh ?? NoOp, skipIfExpanded: alert);
    }

    // --- reposo ---

    /// <summary>
    /// Reposo del contenedor (001 MOD RF-2): pieza inactiva o nada según el ajuste; ante
    /// supresión, oculto (la pieza también se suprime, RF-14).
    /// </summary>
    private void PresentRest()
    {
        if (Suppressed() && !HasExclusive()) { SnapHidden(); return; }
        if (!ReturnToInactive()) { GoHidden(); return; }
        ShowInactive();
    }

    /// <summary>¿Hay alguna PANTALLA con algo usable? (001 MOD RF-9): sin pantalla usable no hay vista que anclar.</summary>
    private bool AnyScreenUsable() => UsableScreenCount() > 0;

    /// <summary>
    /// El toggle «volver a inactivo» decide el reposo: pieza negra visible o nada
    /// (001 MOD RF-2, por defecto inactivo visible). Sin funcionalidades usables no hay
    /// pieza: no se ancla una caja vacía.
    /// </summary>
    private bool ReturnToInactive() =>
        SettingsManager.Current.IslandReturnToInactive && AnyScreenUsable();

    /// <summary>
    /// Estado inactivo (001 MOD RF-11, RF-16): pill negra más estrecha que el compacto, sin
    /// ninguna vista de contenido; el hover solo la agranda y el clic abre la última usable.
    /// </summary>
    private void ShowInactive()
    {
        // Diagnóstico del contrato de actividad: la pieza no debería reposar con media
        // reproduciendo (001 MOD RF-4). Si aparece en el log, la lista de activos dejó
        // pasar una sesión.
        if (SettingsManager.Current.IslandVisibilityMode == 0 && !_expanded
            && _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            Logger.Warn("Island: reposo en la pieza con media reproduciendo " +
                "(snapshot={Snap}, permitidas reproduciendo={Playing}); revisar la lista de activos",
                _music?.Id, NewestPlaying() != null);
        ClearTemporaryNotice();
        // El repliegue parte de la geometría expandida: el compacto no debe florecer de
        // paso mientras el ancho morfa al de la pieza (001 MOD RF-16).
        _collapseFromExpanded = _expanded || _p > 0.02;
        _hidingViaCompact = false;
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        _inactiveHot = false;
        // El reposo olvida el alto compartido: el próximo despliegue se mide de nuevo
        // (001 MOD RF-15).
        ResetSharedHeight();
        SetInactiveRest(true);
        // Animado cuando la pieza todavía no domina la vista (contenido que fundir) o
        // cuando la geometría aún no es la de reposo (expandido que morfar): en ambos
        // casos hay algo que interpolar.
        if (AnimationsEnabled && IsBoxShown && (_inactiveT < 1 || _p > 0.05))
        {
            _pT = 0;
            _pv = 0;
            _qT = 1;
            PositionTopCenter();
            IslandBox.Visibility = Visibility.Visible;
            UpdateRotationPauseState();
            UpdateLine();
            EnsureLoop();
            return;
        }
        // Reposo inmediato (animaciones off o caja recién aparecida): sin contenido que
        // fundir, la pieza se limpia de golpe.
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
    /// Fija el objetivo del reposo inactivo. Hacia la pieza (on) funde la vista actual hacia
    /// ella; hacia contenido delega en <see cref="EnterContent"/>. Con la caja oculta (o las
    /// animaciones apagadas) el progreso salta directo: no hay contenido que fundir ni
    /// morfología que interpolar, y arrancaría un ancho equivocado.
    /// </summary>
    private void SetInactiveRest(bool on)
    {
        if (!on) { EnterContent(); return; }
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
    /// Punto ÚNICO de entrada al contenido (compacto o expandido, 001 MOD RF-16): mientras
    /// hay una funcionalidad a la vista, el reposo inactivo no aplica ni deja residuos. El
    /// reloj de reposo se reapunta a contenido y viaja desde donde esté: si la pieza ya
    /// domina la vista, el contenido se funde hacia fuera de ella; si el repliegue iba a
    /// medio camino, se da la vuelta desde su progreso actual (nada de saltos: pegar el
    /// salto a 0 era justo el corte seco que se veía al reapuntar en vuelo).
    /// </summary>
    private void EnterContent()
    {
        _collapseFromExpanded = false;
        _inactiveShown = false;
        _inactiveTt = 0;
        if (_inactiveT <= 0) return;
        if (AnimationsEnabled && IsBoxShown) { EnsureLoop(); return; }
        // Sin animaciones (o con la caja fuera del árbol) no hay nada que interpolar: el
        // progreso se pega al destino.
        _inactiveT = 0;
    }

    /// <summary>
    /// Cierre de la transición a inactivo: ahora sí se retira el contenido residual
    /// (001 MOD RF-11) y se apagan indicadores. Se llama solo cuando el progreso llegó a 1,
    /// jamás a mitad de vuelo.
    /// </summary>
    private void FinishInactive()
    {
        // La pieza es la vista final de este repliegue: la próxima reapertura entra por la
        // ruta normal (y así el contenido puede florecer).
        _collapseFromExpanded = false;
        ClearInactiveResidue();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    /// <summary>
    /// Limpieza real del estado inactivo (001 MOD RF-11): no basta con fundir las capas por
    /// opacidad, el contenido residual (grillas compactas con la última funcionalidad,
    /// carátula, fondo, títulos y textos del temporizador) se retira de verdad. La
    /// restauración corre por las rutas normales (ApplyContentVisibility + el repintado de
    /// la ficha al mostrar compacto o expandido).
    /// </summary>
    private void ClearInactiveResidue()
    {
        HideAllContentLayers();
        ClearMusicResidue();
        TimerRemaining.Text = "00:00:00";
        TimerProgressFill.Width = 0;
        TimerRunRemaining.Text = "00:00:00";
        RestoreExpandedHomes();
        ApplyFrame();
    }

    // --- transiciones instantáneas (sin animaciones) ---

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        // Oculto no hay alto que conservar: el próximo despliegue mide de nuevo
        // (001 MOD RF-15).
        ResetSharedHeight();
        if (Math.Abs(_p) > 0.05)
        {
            _expanded = false; _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return;
        }
        _qT = 0;
        EnsureLoop();
    }

    private void SnapCompact()
    {
        // Estado aplicado de golpe: no hay vista aplicada que comparar (la próxima
        // resolución vuelve a presentarla).
        _appliedPresentation = null;
        _inactiveShown = false;
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
        SyncEq();
        UpdateVisibleRefresh();
    }

    private void SnapHidden()
    {
        _appliedPresentation = null;
        _hidingViaCompact = false;
        _inactiveShown = false;
        _collapseFromExpanded = false;
        ResetSharedHeight();
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
        SyncEq();
        UpdateVisibleRefresh();
    }

    // ------------------------------------------------------------------
    // Rutas de repliegue (re-resuelven la vista: ya no la deciden ellas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Algo dejó de tener vista (se apagó, se cerró, dejó de ser presentable) o hay que
    /// replantear la vista: el contenedor re-resuelve con la lista de activos. Replegar no
    /// es decidir: si sigue habiendo algo activo, se muestra su compacto.
    /// </summary>
    private void HidePerMode()
    {
        UpdateRotationPauseState();
        // Alerta final: es exclusiva y modal hasta que se cierra (002 RF-2); la propia
        // política la vuelve a poner, así que no hay nada que replantear.
        if (_timer.State == IslandTimerState.Alerting) return;
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// La vista vigente se repliega a lo que toque: la activa que siga viva o el reposo
    /// (001 MOD RF-2, RF-4). Es el punto por el que entraban las seis decisiones dispersas.
    /// </summary>
    private void ShowInactiveOrHidden()
    {
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// El puntero se alejó del Island expandido Y ya venció su tolerancia
    /// (<see cref="HoverLeaveGraceMs"/>): el expandido del usuario termina aquí y la vista
    /// se re-resuelve con la lista de activos —con música sonando el compacto es la música,
    /// nunca la pieza (001 MOD RF-4)—.
    /// </summary>
    private void CollapseFromHover()
    {
        if (!_expanded) return;
        // Alerta de fin (exclusiva): persistente hasta X o reinicio, aunque el ratón se
        // vaya (001 MOD RF-4).
        if (_timer.State == IslandTimerState.Alerting) return;
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// Funcionalidad que SOSTIENE la vista vigente según la lista de activos (null si no hay
    /// ninguna). Es lo que abre el clic en la pieza y el punto de partida de los repliegues
    /// que conservan contenido: una sola resolución para todos (001 MOD RF-4).
    /// </summary>
    private IIslandFeature? ResolveActiveVigenteForVisible()
    {
        var result = IslandPresentation.Resolve(PresentationInput(userExpanded: false));
        return result.FeatureId is { } id ? FeatureById(id) : null;
    }

    /// <summary>
    /// ¿La funcionalidad sigue sosteniendo la vista AHORA? (actividad propia o su aviso
    /// vivo). Con una pantalla combinada delante la sostiene CUALQUIERA de sus miembros
    /// (change island-pantallas RF-4).
    /// </summary>
    private bool FeatureSustainsView(IIslandFeature feature)
    {
        if (_contentMode == IslandContentMode.Screen && ScreenIsCombined()) return ScreenSustainsView();
        return SingleFeatureSustainsView(feature);
    }

    /// <summary>
    /// Expande la última usable con repliegue seguro: si la elegida deja de ser presentable
    /// en el último instante, prueba la siguiente usable; si no hay ninguna, resuelve el
    /// reposo sin abrir una caja vacía (RF-3, RF-9, RF-16).
    /// </summary>
    private bool ExpandLastUsable()
    {
        // El dictado manda (RF-10): con una sesión en marcha no se abre ninguna pantalla
        // encima de su tarjeta.
        if (DictationActive()) return false;
        if ((Suppressed() && !HasExclusive()) || !SettingsManager.Current.IslandEnabled) { SnapHidden(); return false; }
        // La unidad de la vista es la PANTALLA (change island-pantallas RF-4): el clic abre
        // la pantalla de la funcionalidad que el usuario tiene DELANTE —el compacto enseña
        // una sola— y, sin compacto a la vista (la pieza de reposo), la de la activa vigente.
        if ((ShownCompactFeature() ?? ResolveActiveVigenteForVisible()) is { } target)
        {
            int index = ResolveScreenIndexFor(target.Id);
            if (index >= 0) _screenIndex = index;
        }
        for (int step = 0; step < _screens.Count; step++)
        {
            int index = WrapUnit(_screenIndex + step, _screens.Count);
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            if (ExpandCurrentScreen()) return true;
        }
        HidePerMode();
        return false;
    }

    // ------------------------------------------------------------------
    // Aviso temporal (001 RF-2, 002 RF-16): una entrada más de la lista
    // ------------------------------------------------------------------

    /// <summary>
    /// Arma el plazo del aviso de la vista que se acaba de presentar. Y con él publica la
    /// ENTRADA en la lista de activos: es lo que hace que la vista siga siendo la deseada
    /// mientras su plazo corra (y que deje de serlo al vencer).
    ///
    /// <list type="bullet">
    /// <item><b>force</b> = true para los contenidos cuyo aviso es SIEMPRE temporal, aunque
    /// el modo sea «Visible mientras activo» (Bluetooth, cargador: su vista es una
    /// notificación, no un estado).</item>
    /// <item><b>restart</b> = false conserva el plazo que ya corría: interactuar —expandir y
    /// volver— o re-presentar el mismo aviso nunca lo prolonga (001 RF-2). Un evento nuevo
    /// (una conexión, una copia, una pista) sí estrena plazo.</item>
    /// </list>
    /// </summary>
    private void ArmTemporaryHide(bool restart = true, bool force = false)
    {
        var now = DateTime.UtcNow;
        // Una acción de la cuenta hecha dentro del expandido deja su aviso PENDIENTE: el
        // plazo del temporizador arranca al presentarse su compacto, no al pulsar Iniciar
        // (002 RF-16), y eso es justo lo que acaba de ocurrir.
        if (_pendingTimerNotice) { restart = true; _pendingTimerNotice = false; }
        string? id = FeatureCardOfMode(_contentMode)?.Id ?? CompactMemberOfCurrentScreen()?.Id;
        // Una vista que ya sostiene su ACTIVIDAD no necesita plazo: la pausa que cuenta como
        // activo o la cuenta pausada son ESTADOS, y su vista no vence. Armarles un aviso las
        // haría re-armar y vencer cada pocos segundos sin que nada cambiara.
        if (id != null && _activity.HasLive(id, now))
        {
            ClearTemporaryNotice();
            return;
        }
        // La regla de armado vive una sola vez, en la política (IslandPolicy.cs): sin aviso
        // que armar, el de esta vista se olvida.
        if (id == null || !IslandNoticePolicy.Arms(force, SettingsManager.Current.IslandVisibilityMode == 1, HasExclusive()))
        {
            ClearTemporaryNotice();
            return;
        }
        if (restart || !_activity.NoticeAlive(id, now))
        {
            int ms = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
            _activity.Pulse(id, now, now.AddMilliseconds(ms), force);
        }
        _noticeFeatureId = id;
        _noticeStarted = _activity.NoticeStarted(id, now);
        _noticeForced = force;
        _noticeUntil = _activity.NoticeExpiry(id, now);
        ScheduleNoticeRetraction();
    }

    /// <summary>
    /// El aviso se expandió: el plazo sigue corriendo, solo se pospone su repliegue hasta que
    /// la vista vuelva a ser compacta. Sin esto el vencimiento moría dentro del expandido y
    /// el aviso se quedaba pegado para siempre.
    /// </summary>
    private void HoldTemporaryNotice() => _noticeVersion++;

    /// <summary>
    /// Cierra el aviso presentado: no hay plazo pendiente que cumplir ni entrada que
    /// sostenga la vista (la lista de activos deja de contarlo y el contenedor
    /// re-resuelve en la siguiente pasada, no antes: olvidar un aviso no cambia la vista).
    /// </summary>
    private void ClearTemporaryNotice()
    {
        _noticeVersion++;
        _noticeCheckActive = false;
        _noticeUntil = DateTime.MinValue;
        _noticeForced = false;
        // Solo el AVISO: si la funcionalidad tiene además actividad viva (una pausa que cuenta
        // como activo, una cuenta pausada), esa sigue sosteniendo su vista.
        if (_noticeFeatureId is { } id) _activity.ForgetNotice(id);
        _noticeFeatureId = null;
        _noticeStarted = DateTime.MinValue;
        _pendingTimerNotice = false;
    }

    /// <summary>
    /// ¿Sigue vivo el aviso de ESTA vista? Con las entradas por funcionalidad ya no puede
    /// confundirse: un aviso del temporizador no declara activo al cargador ni a Bluetooth.
    /// </summary>
    private bool NoticeAliveFor(IslandContentMode mode)
    {
        string? id = FeatureCardOfMode(mode)?.Id;
        return id != null && _activity.NoticeAlive(id, DateTime.UtcNow);
    }

    /// <summary>
    /// Programa el vencimiento del aviso con un temporizador de UN SOLO disparo (nada de
    /// latido): a la hora absoluta del plazo se repliega. Si el disparo se perdiera, la
    /// recuperación de 5 s lo detecta y lo cumple.
    /// </summary>
    private void ScheduleNoticeRetraction()
    {
        int version = ++_noticeVersion;
        TimeSpan wait = _noticeUntil - DateTime.UtcNow;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        ScheduleNoticeCheck(version, wait);
    }

    /// <summary>
    /// Deja armado un único chequeo del aviso. <c>_noticeCheckActive</c> evita cadenas
    /// duplicadas cuando la recuperación lenta repara un disparo perdido.
    /// </summary>
    private void ScheduleNoticeCheck(int version, TimeSpan wait)
    {
        _noticeCheckActive = true;
        _ = Task.Delay(wait).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            _noticeCheckActive = false;
            RetractTemporaryNotice(version);
        }));
    }

    /// <summary>
    /// Repliegue del aviso temporal al vencer su plazo. El puntero ni las actualizaciones de
    /// datos reinician el plazo, pero el aviso tampoco se cierra bajo el cursor ni mientras
    /// está expandido: se reintenta hasta que la vista vuelva a ser compacta y el ratón no
    /// estorbe, así nunca se queda pegado (001 RF-2, 002 RF-8/RF-16).
    /// </summary>
    private void RetractTemporaryNotice(int version)
    {
        if (_disposed || version != _noticeVersion) return;
        var now = DateTime.UtcNow;
        string? id = _noticeFeatureId;
        var step = IslandNoticePolicy.OnExpired(
            forced: _noticeForced,
            temporalMode: SettingsManager.Current.IslandVisibilityMode == 1,
            hasExclusive: HasExclusive(),
            boxShown: IsBoxShown,
            expanded: _expanded,
            pointerOver: IsMouseOverBoxOrStrip());
        if (step == IslandNoticeStep.Retry)
        {
            // La vista no puede replegarse ahora: se le da un plazo corto MÁS al mismo
            // aviso —renovando, para no tocar su instante de nacimiento— y se reintenta.
            if (id != null && !_activity.NoticeAlive(id, now))
                _activity.Renew(id, now.AddMilliseconds(250));
            ScheduleNoticeCheck(version, TimeSpan.FromMilliseconds(250));
            return;
        }
        ClearTemporaryNotice();
        if (step == IslandNoticeStep.Forget) return;
        RefreshPresentation();
    }
}
