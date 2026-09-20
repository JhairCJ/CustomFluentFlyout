// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Banda de contenido vigente del contenedor (001 MOD RF-11): qué funcionalidad
/// está a la vista. Sustituye a los enteros mágicos 0/1/2 que se comparaban a
/// mano en cada partial.
/// </summary>
internal enum IslandContentMode
{
    Media = 0,
    Timer = 1,
    Apps = 2,
    Shelf = 3,
    Calendar = 4,
    Bluetooth = 5,
    /// <summary>Pantalla COMBINADA (varias funcionalidades a la vez, change island-pantallas).</summary>
    Screen = 6,
    Clipboard = 7,
    /// <summary>Clima del lugar configurado (change island-clima).</summary>
    Weather = 8,
    /// <summary>Cargador del equipo (change island-cargador).</summary>
    Power = 9,
}

/// <summary>
/// Ruta ÚNICA de presentación del contenedor: cómo se llega al compacto y al
/// expandido, escrita una sola vez para las tres funcionalidades.
///
/// <para>Antes música, temporizador y cajón repetían la misma secuencia
/// (calcular el repliegue en curso, cancelar el reposo inactivo, aplicar
/// contenido, medir, snap o animar, armar el aviso) y cualquier retoque había
/// que hacerlo tres veces: bastaba olvidar una para que las tres vistas
/// divergieran. Ahora cada funcionalidad aporta SOLO su contenido
/// (<c>present</c>) y su comprobación de disponibilidad; la coreografía del
/// contenedor vive aquí:</para>
///
/// <list type="bullet">
/// <item><b>Compacto</b> — guardas → contenido → geometría de reposo → snap o
/// repliegue en dos fases (001 MOD RF-16) → plazo del aviso temporal (001 RF-2).</item>
/// <item><b>Expandido</b> — guardas → contenido → geometría expandida → snap o
/// muelles hacia p=1, q=1.</item>
/// </list>
///
/// <para>Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>,
/// la máquina de estados en <c>IslandWindow.States.cs</c> y el motor de
/// animación por frame en <c>IslandWindow.Frame.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// ¿Hay un repliegue desde el expandido en curso? El compacto se alcanza
    /// PASANDO por la pieza inactiva (fase 1 → fase 2), nunca apareciendo de
    /// golpe bajo el contenido que se repliega (001 MOD RF-16).
    /// </summary>
    private bool IsCollapsingFromExpanded() =>
        _expanded || _p > 0.02 || _pendingCompactFeature != null;

    /// <summary>
    /// Punto ÚNICO de entrada a la vista compacta. La funcionalidad aporta su
    /// disponibilidad (guardas propias, ya evaluadas por quien llama) y el
    /// contenido de <paramref name="present"/>; el contenedor decide la
    /// geometría, la animación y el aviso temporal.
    ///
    /// <para><paramref name="feature"/> es la funcionalidad que sostiene la
    /// vista: es la que el repliegue en dos fases reabre al llegar a la pieza.
    /// Con <c>null</c> el compacto aparece sin fase 2 (vista huérfana: no hay
    /// actividad que reabrir).</para>
    ///
    /// <para>Si el repliegue parte del expandido, la fase 1 (hasta la pieza) NO
    /// presenta contenido nuevo: encoge y apaga lo que el usuario está mirando, y
    /// el compacto entra en la fase 2, sobre la pieza y ya sin intercambio a la
    /// vista. Así replegar un temporizador no enseña media de golpe y luego la
    /// música otra vez: se ve UNA transición, la del contenido que había.</para>
    ///
    /// <para><paramref name="forceNotice"/> y <paramref name="restartNotice"/> son
    /// para los contenidos cuyo aviso es SIEMPRE temporal (dispositivos Bluetooth,
    /// change island-bluetooth-conectado RF-1): el aviso vence aunque el modo sea
    /// «Visible mientras activo», y re-presentarlo no reinicia su plazo —solo un
    /// evento nuevo lo hace—.</para>
    ///
    /// <para>La vista del Island es una PANTALLA (change island-pantallas): la
    /// funcionalidad adopta la suya, pero el COMPACTO no agrupa —enseña la vista rica de
    /// esa funcionalidad, una sola, aunque su pantalla lleve varias— y es el clic
    /// posterior el que abre la pantalla entera. Una funcionalidad que no está en
    /// ninguna pantalla no tiene vista: el contenedor resuelve otra cosa —o el reposo—
    /// en vez de presentarla.</para>
    /// </summary>
    private void ShowCompactView(IslandContentMode mode, IIslandFeature? feature, Action present,
        bool forceNotice = false, bool restartNotice = true)
    {
        // El repliegue se mide ANTES de tocar nada: define si el compacto es una
        // transición en dos fases (venía del expandido) o una entrada directa.
        bool collapsing = IsCollapsingFromExpanded();
        _hidingViaCompact = false;
        if (feature != null) SelectFeature(feature.Id);
        // Entrar al contenido cancela el reposo inactivo: sin esto el compacto se
        // pintaría sobre el contenido ya desvanecido (001 MOD RF-16).
        SetInactiveRest(false);
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        // PANTALLAS: la vista la manda la pantalla de la funcionalidad, y el compacto
        // enseña UNA sola de sus funcionalidades (su vista rica): agrupar es cosa del
        // expandido, que es lo que abre el clic. Sin pantalla no hay nada que presentar:
        // se resuelve la vista por las vías normales (otra pantalla, el temporizador o el
        // reposo). La excepción es una exclusiva (la alerta del temporizador), que
        // conserva su vista propia.
        if (feature != null && !AdoptScreenFor(feature, expanded: false, ref mode, ref present)
            && !ScreenlessFeatureKeepsOwnView(feature))
        {
            _expanded = false;
            ShowInactiveOrHidden();
            return;
        }
        // Repliegue en dos fases con la pieza de tránsito (001 MOD RF-16): la fase
        // 1 baja la geometría hasta la pieza con el contenido vigente intacto y la
        // fase 2 (TryReopenFromInactive) presenta el compacto nuevo ya sobre ella.
        // Sin side effects si no procede (ya en la pieza, sin animaciones o sin
        // caja), así que puede decidirse antes de presentar nada.
        bool throughPiece = AnimationsEnabled && collapsing && feature != null
            && BeginCollapseThroughInactive(feature);
        if (!throughPiece)
        {
            // La vista compacta debe estar resuelta antes de pintar. Mantener _expanded
            // vivo aquí hacía que una actualización intermedia de la pantalla leyera el
            // estado expandido y mezclara paneles de las dos presentaciones.
            _expanded = false;
            _contentMode = mode;
            present();
            ApplyContentVisibility();
        }
        _expanded = false;
        // Las flechas de navegación solo existen en expandido: se apagan ya, en el
        // mismo turno, en vez de esperar al siguiente latido del contenedor.
        UpdateArrows();
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else if (!throughPiece) SetCompactFrame();
        // «Aviso temporal» (001 RF-2, 002 RF-16): la vista compacta vence al plazo
        // configurado. restart:false conserva el plazo que ya corría, así
        // interactuar (expandir y volver) nunca prolonga el aviso.
        ArmTemporaryHide(restart: restartNotice && !collapsing, force: forceNotice);
    }

    /// <summary>
    /// Punto ÚNICO de entrada a la vista expandida. <paramref name="guard"/> se
    /// evalúa justo después de cancelar el reposo inactivo —antes de aplicar
    /// contenido y geometría— para las funcionalidades que necesitan vetar la
    /// expansión con el estado ya normalizado (media con una exclusiva vigente).
    ///
    /// <para><paramref name="skipIfExpanded"/> en false fuerza el rearme del
    /// frame aunque la caja ya estuviera expandida: es lo que necesita el aviso
    /// final del temporizador, que debe imponerse sobre la vista vigente
    /// (002 RF-2, RF-6).</para>
    ///
    /// <para>Como en el compacto, la vista es la PANTALLA de la funcionalidad: con una
    /// pantalla combinada el expandido es su fila de columnas (de izquierda a derecha)
    /// y, sin pantalla, la funcionalidad no abre nada (change island-pantallas).</para>
    /// </summary>
    private void ShowExpandedView(IslandContentMode mode, IIslandFeature? feature, Action present,
        bool skipIfExpanded = true, Func<bool>? guard = null)
    {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        // Expandir no cancela el aviso: solo pospone su repliegue conservando el
        // plazo que le quedaba (001 RF-2).
        HoldTemporaryNotice();
        _hidingViaCompact = false;
        bool wasExpanded = _expanded;
        if (feature != null) SelectFeature(feature.Id);
        SetInactiveRest(false);
        // PANTALLAS: la vista la manda la pantalla de la funcionalidad (columnas si es
        // combinada) y sin pantalla no hay nada que abrir: la vista se resuelve por las
        // vías normales en vez de abrir la funcionalidad suelta. La excepción es una
        // exclusiva (la alerta del temporizador), que conserva su vista propia.
        if (feature != null && !AdoptScreenFor(feature, expanded: true, ref mode, ref present)
            && !ScreenlessFeatureKeepsOwnView(feature))
        {
            HidePerMode();
            return;
        }
        if (guard != null && !guard()) return;
        _contentMode = mode;
        // El contenido y la composición deben conocer el estado final antes de
        // renderizar. Si se marcaba después, una pantalla combinada entraba por la rama
        // compacta, volvía a llamar a ExpandCurrentScreen y además se medía con el
        // ancho compacto.
        _expanded = true;
        present();
        ApplyContentVisibility();
        // Con la vista ya expandida, las flechas de navegación entran en el mismo
        // turno.
        UpdateArrows();
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            SnapExpandedFrame();
            return;
        }
        if (wasExpanded && skipIfExpanded) return; // ya expandido: solo cambió el contenido
        SetExpandedFrame();
    }

    // --- geometría compartida de los dos extremos ---

    /// <summary>
    /// Destino de reposo del compacto: muelles hacia p=0 con la caja viva. Se usa
    /// tanto al entrar al compacto sin repliegue como al descartar una fase 2.
    /// </summary>
    private void SetCompactFrame()
    {
        _pT = 0;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    /// <summary>Arranque animado del expandido: muelles hacia p=1, q=1.</summary>
    private void SetExpandedFrame()
    {
        _pT = 1;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    /// <summary>Expandido sin animación: estado final aplicado de golpe.</summary>
    private void SnapExpandedFrame()
    {
        SetInactiveRest(false);
        _p = _pT = 1; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }
}
