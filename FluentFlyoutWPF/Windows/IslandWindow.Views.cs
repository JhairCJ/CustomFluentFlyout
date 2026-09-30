// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
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
    /// <summary>Dictado por voz con la tecla mantenida (spec 006).</summary>
    Dictation = 10,
}

/// <summary>
/// Ruta ÚNICA de presentación del contenedor: cómo se llega al compacto y al
/// expandido, escrita una sola vez para todas las funcionalidades.
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
/// muelles → plazo del aviso temporal (001 RF-2).</item>
/// <item><b>Expandido</b> — guardas → contenido → geometría expandida → snap o
/// muelles hacia p=1, q=1.</item>
/// </list>
///
/// <para>La CARA se presenta SIEMPRE y en el mismo turno (change
/// island-lista-de-activos): un evento activo se ve de inmediato, y ninguna caja
/// puede quedarse sin capas. Antes el repliegue desde el expandido podía saltarse
/// la presentación y dejar la píldora negra vacía —el «bloque negro» del
/// temporizador—.</para>
///
/// <para>Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>,
/// la máquina de estados en <c>IslandWindow.States.cs</c> y el motor de
/// animación por frame en <c>IslandWindow.Frame.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Punto ÚNICO de entrada a la vista compacta. La funcionalidad aporta su
    /// disponibilidad (guardas propias, ya evaluadas por quien llama) y el
    /// contenido de <paramref name="present"/>; el contenedor decide la
    /// geometría, la animación y el aviso temporal.
    ///
    /// <para><paramref name="forceNotice"/> y <paramref name="restartNotice"/> son
    /// para los contenidos cuyo aviso es SIEMPRE temporal (dispositivos Bluetooth, el
    /// cargador: change island-bluetooth-conectado RF-1): el aviso vence aunque el modo
    /// sea «Visible mientras activo», y re-presentarlo no reinicia su plazo —solo un
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
        // Una vista que se presenta es una vista que se ve: si la ventana quedó
        // Collapsed (supresión, apagado, repliegue a nada), se vuelve a asomar aquí
        // mismo. Sin esto, un aviso que llega con la ventana retirada pintaba su
        // tarjeta dentro de una ventana que no se veía.
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hidingViaCompact = false;
        if (feature != null) SelectFeature(feature.Id);
        // Entrar al contenido cancela el reposo inactivo: sin esto el compacto se
        // pintaría sobre el contenido ya desvanecido (001 MOD RF-16).
        SetInactiveRest(false);
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        // PANTALLAS: la vista la manda la pantalla de la funcionalidad, y el compacto
        // enseña UNA sola de sus funcionalidades (su vista rica): agrupar es cosa del
        // expandido, que es lo que abre el clic. Sin pantalla no hay nada que presentar:
        // se resuelve la vista por las vías normales (otra pantalla, otra activa o el
        // reposo). Las excepciones conservan su vista propia: una exclusiva (la alerta del
        // temporizador) y un AVISO (Bluetooth, cargador, dictado), que no es pantalla ni se
        // navega.
        if (feature != null && !AdoptScreenFor(feature, expanded: false, ref mode, ref present)
            && !ScreenlessFeatureKeepsOwnView(feature))
        {
            _expanded = false;
            HidePerMode();
            return;
        }
        // La cara y la geometría, en el MISMO turno: la vista compacta debe estar resuelta
        // antes de pintar (mantener _expanded vivo aquí hacía que una actualización
        // intermedia leyera el estado expandido y mezclara paneles de las dos
        // presentaciones).
        _expanded = false;
        _contentMode = mode;
        present();
        ApplyContentVisibility();
        // Las vistas que llegan directamente desde una función también deben
        // actualizar la caché de presentación. Si no, un cierre rápido podía
        // comparar contra el estado anterior (inactivo/oculto) y no aplicar la salida.
        _appliedPresentation = new IslandPresentationResult(
            IslandDesiredView.Compact, CurrentViewFeatureId(), restartNotice, forceNotice);
        // Las flechas de navegación solo existen en expandido: se apagan ya, en el
        // mismo turno, en vez de esperar al siguiente latido del contenedor.
        UpdateArrows();
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else SetCompactFrame();
        // «Aviso temporal» (001 RF-2, 002 RF-16): la vista compacta publica su aviso en la
        // lista de activos con el plazo configurado. restart:false conserva el plazo que ya
        // corría, así interactuar (expandir y volver) nunca prolonga el aviso.
        ArmTemporaryHide(restart: restartNotice, force: forceNotice);
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
        _appliedPresentation = new IslandPresentationResult(
            IslandDesiredView.Expanded, CurrentViewFeatureId(), false, false);
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
    /// Destino de reposo del compacto: muelles hacia p=0 con la caja viva.
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
