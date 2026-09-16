// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Motor de animación del Island: muelles por frame (sin Storyboards) sobre una
/// caja única y todo el cálculo de geometría/opacidad de sus estados.
///
/// Reglas que sostiene:
/// - p (0 compacto -> 1 expandido) y q (0 oculto -> 1 visible) son muelles
///   subamortiguados escalados con la duración global de animaciones.
/// - El estado inactivo (pieza negra estrecha) no es otra caja: es el mismo
///   contenedor con ancho de reposo estrecho y contenido desvanecido, animado
///   con el mismo reloj para que no haya saltos ni dos estados visibles a la vez.
/// - Con las animaciones apagadas todo se aplica de golpe (Snap*).
/// Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>.
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Duración de la transición compacto-con-contenido <-> pieza inactiva:
    /// sigue la velocidad global de animaciones para que sea coherente con el
    /// resto del Island (001 MOD RF-16).
    /// </summary>
    private static double InactiveTransitionSeconds =>
        Math.Clamp(MainWindow.getDuration(), 120, 700) / 1000.0;

    // Apple-ish: muelle subamortiguado suave, escalado con la duración global.
    private void GetSpring(out double kP, out double cP, out double kQ, out double cQ)
    {
        double configuredDuration = MainWindow.getDuration();
        double durationScale = configuredDuration > 0 ? configuredDuration / 300.0 : 1.0;
        double frequencyScale = 1.0 / (durationScale * durationScale);
        double dampingScale = 1.0 / durationScale;
        kP = 520 * frequencyScale; cP = 34 * dampingScale;
        kQ = 200 * frequencyScale; cQ = 28 * dampingScale; // ambos estilos emergen desde el centro como Island
        if (IsNotch) kP *= 1.05;
    }

    private void EnsureLoop()
    {
        if (_loopOn) return;
        _loopOn = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_loopOn) return;
        _loopOn = false;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnFrame;
    }

    private TimeSpan _lastTick = TimeSpan.Zero;

    private void OnFrame(object? s, EventArgs e)
    {
        var args = e as RenderingEventArgs;
        TimeSpan now = args?.RenderingTime ?? TimeSpan.Zero;
        if (now == TimeSpan.Zero) now = TimeSpan.FromTicks(Environment.TickCount64 * 10000);
        double dt;
        if (_lastTick == TimeSpan.Zero || now <= _lastTick) dt = 1.0 / 60.0;
        else dt = Math.Clamp((now - _lastTick).TotalSeconds, 1.0 / 240.0, 1.0 / 25.0);
        _lastTick = now;

        GetSpring(out double kP, out double cP, out double kQ, out double cQ);
        Step(ref _p, ref _pv, _pT, kP, cP, dt);
        Step(ref _q, ref _qv, _qT, kQ, cQ, dt);
        // Progreso hacia la pieza inactiva: avance lineal a velocidad constante
        // (ease-in-out lo aporta Smooth01 al pintar). Reapuntar a mitad de vuelo
        // mantiene la misma velocidad y jamás da un salto (001 MOD RF-16).
        if (_inactiveT != _inactiveTt)
        {
            double step = dt / InactiveTransitionSeconds;
            _inactiveT += Math.Sign(_inactiveTt - _inactiveT) * step;
            if (_inactiveTt > _inactiveT ? _inactiveT >= _inactiveTt : _inactiveT <= _inactiveTt)
                _inactiveT = _inactiveTt;
        }
        // La altura del expandido persigue a su objetivo: cambios de contenido glideas, no saltos.
        _hexpShown += (_hexp - _hexpShown) * Math.Clamp(dt * 10, 0, 1);
        if (Math.Abs(_hexp - _hexpShown) < 0.5) _hexpShown = _hexp;
        // Micro-crecimiento vivo del hover (pieza inactiva y caja en reposo).
        double hotTarget = _inactiveHot ? 1 : 0;
        if (_inactiveHotT != hotTarget)
        {
            _inactiveHotT += (hotTarget - _inactiveHotT) * Math.Clamp(dt * 12, 0, 1);
            if (Math.Abs(_inactiveHotT - hotTarget) < 0.01) _inactiveHotT = hotTarget;
        }
        if (_popPlaying) StepPop(dt);
        ApplyFrame();

        bool pSettled = Math.Abs(_p - _pT) < 0.002 && Math.Abs(_pv) < 0.02;
        bool qSettled = Math.Abs(_q - _qT) < 0.002 && Math.Abs(_qv) < 0.02;
        if (pSettled) { _p = _pT; _pv = 0; }
        if (qSettled) { _q = _qT; _qv = 0; }

        bool popSettled = !_popPlaying;
        bool hSettled = _hexpShown == _hexp;
        bool hotSettled = _inactiveHotT == (_inactiveHot ? 1 : 0);
        bool inactSettled = _inactiveT == _inactiveTt;

        if (pSettled && qSettled && popSettled && hSettled && hotSettled && inactSettled)
        {
            // La pieza inactiva ya es la vista asentada. Recién AHORA decide el
            // contenedor si se reabre al compacto —fase 2 de un repliegue en dos
            // fases o actividad vigente en «Visible mientras activo»— sin limpiar
            // residuos: el mismo contenido reaparece en el compacto (001 MOD RF-16).
            if (AtInactiveRest && TryReopenFromInactive())
                return; // el loop sigue: la reapertura acaba de empezar
            StopLoop();
            _lastTick = TimeSpan.Zero;
            if (_inactiveShown && _inactiveT == 1)
            {
                // La pieza inactiva ya está en su forma final: recién ahora se
                // retira el contenido residual (001 MOD RF-11, RF-16).
                FinishInactive();
            }
            if (_qT == 0 && _q == 0)
            {
                IslandBox.Visibility = Visibility.Collapsed;
                if (_wasSuppressed) Visibility = Visibility.Collapsed;
                UpdateLine();
            }
            // Si llegamos a compacto vía hidingViaCompact y no hay q pendiente, ya se ocultó arriba
        }
        else if (_qT == 0 && _q <= 0.12)
        {
            _hidingViaCompact = false;
            IslandBox.Visibility = Visibility.Collapsed;
            UpdateLine();
        }
    }

    private static void Step(ref double x, ref double v, double target, double k, double c, double dt)
    {
        double a = (target - x) * k - v * c;
        v += a * dt;
        x += v * dt;
        x = Math.Clamp(x, -0.15, 1.15); // deja un poco de overshoot visible
    }

    private double _popV;
    private double _popPhase; // 0 ida, 1 vuelta
    private void StepPop(double dt)
    {
        // Ida 0->1 rápida, vuelta 1->0 amortiguada: un solo ciclo
        if (_popPhase == 0)
        {
            double a = (1 - _pop) * 900 - _popV * 28;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop >= 0.995) { _pop = 1; _popV = 0; _popPhase = 1; }
        }
        else
        {
            double a = (0 - _pop) * 500 - _popV * 32;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop <= 0.005) { _pop = 0; _popV = 0; _popPlaying = false; _popPhase = 0; }
        }
    }

    /// <summary>
    /// Mide el alto real del expandido con el ancho efectivo y fija el objetivo;
    /// el loop lo glidea (o lo pega, sin animaciones). El contenido del
    /// temporizador manda por medida; música conserva su alto configurado.
    /// </summary>
    private void SyncMeasuredHeight()
    {
        try
        {
            // Medir la altura expandida real con el ancho configurado.
            // ExpandedLayer está siempre en el árbol (Opacity 0 cuando compacto),
            // así que es medible.
            ExpandedLayer.Measure(new Size(ContentExpandedWidth, double.PositiveInfinity));
            double measuredHeight = ExpandedLayer.DesiredSize.Height; // DesiredSize ya incluye el Margin vertical
            // ponytail: el contenido timer manda por medida (sin huecos); música mantiene su ajuste fijo.
            bool timerContent = TimerExpanded.Visibility == Visibility.Visible
                || TimerRunPanel.Visibility == Visibility.Visible
                || TimerAlert.Visibility == Visibility.Visible;
            double h = IsNotch || timerContent ? measuredHeight : ContentExpandedHeight;
            double old = _hexp;
            if (h > (timerContent ? 34 : 60) && h < 260) _hexp = h;
            // El objetivo manda: si cambió, correr frames (o snapping). Si no
            // cambió, ni se toca el loop: música en reposo ni se entera.
            if (_hexp != old)
            {
                if (!AnimationsEnabled || (!IsBoxShown && _qT == 0)) _hexpShown = _hexp;
                else EnsureLoop();
            }
        }
        catch { }
    }

    private static double Smooth01(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private void SnapFrame()
    {
        // Estado base coherente antes del primer frame
        _p = _pT; _q = _qT;
        _pv = _qv = 0;
        _inactiveT = _inactiveTt = _inactiveShown ? 1 : 0;
        _pendingCompactFeature = null;
        _collapseFromExpanded = false;
        _hexpShown = _hexp;
        _inactiveHotT = _inactiveHot ? 1 : 0;
        ApplyFrame();
    }

    /// <summary>
    /// Arranca un repliegue CON contenido en dos fases (001 MOD RF-16):
    ///
    /// <list type="number">
    /// <item>fase 1 — el expandido se contrae y su contenido se desvanece hasta
    /// la pieza inactiva (el cuadrado negro estrecho);</item>
    /// <item>fase 2 — la pieza se reabre al compacto con su contenido, disparada
    /// al asentarse la fase 1 (<see cref="OnFrame"/>).</item>
    /// </list>
    ///
    /// Se usa el mismo reloj de reposo (<c>_inactiveT</c>) para las dos fases, así
    /// que no hay dos estados visibles a la vez ni saltos de geometría. Devuelve
    /// false cuando ya se está en el compacto (sin geometría que replegar): ahí el
    /// compacto se muestra por su ruta normal.
    ///
    /// <para><paramref name="target"/> es la funcionalidad a la que hay que
    /// reabrir en la fase 2: <see cref="TryReopenFromInactive"/> la consume al
    /// llegar a la pieza, de modo que ninguna ruta puede quedarse pegada en el
    /// reposo teniendo algo activo que mostrar.</para>
    /// </summary>
    private bool BeginCollapseThroughInactive(IIslandFeature target)
    {
        if (!AnimationsEnabled || !IsBoxShown) return false;
        if (_p <= 0.02 && _inactiveT <= 0.02) return false;
        _pendingCompactFeature = target;
        _collapseFromExpanded = true;
        _inactiveShown = true;  // la pieza es la vista intermedia (fase 1)
        _inactiveTt = 1;
        _hidingViaCompact = false;
        _pT = 0;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateRotationPauseState();
        EnsureLoop();
        return true;
    }

    /// <summary>
    /// Pinta el frame completo del contenedor: geometría (ancho, alto, radio,
    /// clip, notch), crossfade de las capas compacta y expandida, fondo y
    /// opacidades del contenido. Se llama en cada frame del loop y también
    /// puntualmente cuando no hay animación en curso.
    /// </summary>
    private void ApplyFrame()
    {
        double p = Math.Clamp(_p, 0, 1);
        double q = Math.Clamp(_q, 0, 1);
        // Progreso hacia la pieza inactiva, con ease-in-out: gobierna a la vez el
        // ancho de reposo y el desvanecido de TODO el contenido, así el paso
        // compacto-con-contenido <-> inactivo es una sola transición suave
        // (001 MOD RF-16) en lugar de un cambio de ancho con borrado seco.
        double inact = Smooth01(_inactiveT);
        double contentOp = 1 - inact;
        // Línea gris con el mismo reloj que la isla (p y q): la isla crece
        // centrada = de adentro hacia afuera, la línea encoge centrada = de
        // afuera hacia adentro. Sigue al más rápido (Max): p termina antes
        // que q al emerger expandido, así la línea es 0 cuando el expandido
        // ya salió. En compacto p=0 y queda igual que antes. Sin tween separado.
        bool allowed = SettingsManager.Current.IslandActivityLine && IsAliveForLine();
        _lineW = allowed ? LineFullWidth * (1 - Math.Max(Smooth01(p), Smooth01(q))) * contentOp : 0;
        ActivityLine.Width = _lineW;
        ActivityLine.Visibility = _lineW > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        MediaStatusDot.Opacity = contentOp;
        // Pop de pista atenúa con q (invisible -> no pulsa)
        double pop = _popPlaying ? _pop * q : 0;
        double exitTailOpacity = _qT == 0
            ? Math.Pow(Smooth01(Math.Clamp((q - 0.12) / 0.20, 0, 1)), 3)
            : 1;
        if (_hidingViaCompact)
            exitTailOpacity *= Math.Pow(Smooth01(Math.Clamp((p - 0.12) / 0.38, 0, 1)), 3);

        bool notch = IsNotch;
        double w, h, notchFillet = 0;
        // Reposo vivo: la pieza inactiva y el compacto respiran con el hover
        // (001 MOD RF-3, RF-16): solo crecen en dimensiones, el contenedor
        // jamás se desplaza de su posición.
        double hotW = 10 * _inactiveHotT;
        if (notch)
        {
            // Notch: mismo reveal que Island: punto central -> compacto (más estrecho
        // por diseño) -> expandido.
            const double notchDot = 26;
            double compactW = Lerp(NotchCompactWidth, InactivePillWidth, inact);
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double baseW = q < 0.32 ? notchDot : Lerp(notchDot, compactW, stretchT);
            w = Lerp(baseW, ContentExpandedWidth, Smooth01(p));
            h = Lerp(ContentCompactHeight, _hexpShown, Smooth01(p));
            // Hover vivo: crece desde el punto y en reposo (también en compacto).
            w += hotW * (1 - Smooth01(p));
            double revealOpacity = Smooth01(Math.Clamp(q / 0.38, 0, 1));
            IslandBox.Opacity = revealOpacity * revealOpacity * exitTailOpacity;
            // El radio hace morph con p: compacto -> expandido sin saltos.
            // ponytail: el ANCHO de orejas lo fija el fillet expandido (constante por estado);
            // la CAÍDA de la cueva hace morph compacto->expandido: elipse tendida -> circular.
            double radius = Math.Min(Lerp(IslandCompactRadius, IslandExpandedRadius, Smooth01(p)), Math.Min(w, h) / 2);
            // ponytail: reach y drop usan la curva del estado actual; el extra (+18 = 25-30%)
            // solo aplica en expandido (escala con p), en compacto no se inyecta ancho.
            double filletNow = Lerp(Math.Clamp(SettingsManager.Current.IslandNotchFilletCompact, 0, 20), Math.Clamp(SettingsManager.Current.IslandNotchFilletExpanded, 0, 20), Smooth01(p));
            double earReach = (Math.Clamp(filletNow, 0, 20) + 18 * Smooth01(p)) * stretchT;
            earReach = Math.Min(earReach, Math.Max(0, (Width - w) / 2 - 2));
            notchFillet = Math.Clamp(filletNow, 0, 20) * stretchT;
            IslandBox.CornerRadius = new CornerRadius(0);
            IslandBox.Width = w + 2 * earReach;
            IslandBox.Height = h;
            // ponytail: las orejas son solo fondo; el contenido vive en el ancho lógico w.
            ExpandedLayer.Width = w;
            ExpandedLayer.Margin = new Thickness(0, _expandedMarginOrig.Top, 0, _expandedMarginOrig.Bottom);
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandBox.RenderTransformOrigin = new Point(0.5, 0);
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.Clip = CreateNotchClip(IslandBox.Width, h, radius, earReach, notchFillet);
            LayoutBackground(IslandBox.Width, h);
        }
        else
        {
            // Pill: oculto -> punto 26px (circular) -> compacto (o pieza inactiva
            // más estrecha, 001 MOD RF-11) -> ancho expandido configurado.
            const double pillDot = 26;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double restW = Lerp(ContentCompactWidth, InactivePillWidth, inact);
            double baseW = q < 0.32 ? pillDot : Lerp(pillDot, restW, stretchT);
            w = Lerp(baseW, ContentExpandedWidth, Smooth01(p));
            h = Lerp(ContentCompactHeight, _hexpShown, Smooth01(p));
            // Hover vivo: crece desde el punto y en reposo (también en compacto).
            w += hotW * (1 - Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            ExpandedLayer.Width = double.NaN;
            ExpandedLayer.Margin = _expandedMarginOrig;
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            IslandBox.Opacity = Smooth01(Math.Clamp(q / 0.38, 0, 1)) * exitTailOpacity;
            // Radio: círculo perfecto mientras es punto, luego morph compacto->expandido.
            // En expandido (p>0.02) siempre pill con el radio de expandido.
            double morphR = Lerp(IslandCompactRadius, IslandExpandedRadius, Smooth01(p));
            double cr = baseW <= pillDot + 0.5 && p < 0.02
                ? pillDot / 2
                : Math.Min(morphR, Math.Min(w, h) / 2);
            IslandBox.CornerRadius = new CornerRadius(cr);
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!notch)
        {
            ApplyIslandClip(w, h, IslandBox.CornerRadius);
            LayoutBackground(w, h);
        }

        // Crossfade de capas + morph del contenido (Apple: el álbum y el título
        // respiran). Una sola ruta para pill y notch: el reveal del contenido
        // depende solo de q, así que bifurcarlo duplicaba 40 líneas idénticas y
        // abría la puerta a que los dos estilos se desincronizaran.
        double stretchT2 = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
        double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
        double dotT2 = Math.Clamp(q / 0.32, 0, 1);
        double compactOp = (1 - Smooth01(Math.Clamp(p * 2.2, 0, 1))) * Smooth01(contentT);
        if (q < 0.32) compactOp = 0;
        else compactOp *= Lerp(0.85, 1, dotT2);
        // Rumbo a la pieza inactiva desde una vista expandida el compacto NO
        // florece —ni al principio ni al final del vuelo—: solo se desvanece el
        // contenido que ya estaba, así expandido → inactivo es UNA sola
        // transición, sin destello de compacto (001 MOD RF-16). La fase 2 de un
        // repliegue en dos fases arranca con la bandera apagada, así que ahí el
        // compacto sí florece; y el repliegue que parte del compacto (p=0) no la
        // enciende, de modo que su contenido sigue desvaneciéndose con el reloj
        // del reposo.
        if (_collapseFromExpanded) compactOp = 0;
        double expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
        CompactLayer.Opacity = compactOp * contentOp;
        CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.88, 1, Smooth01(contentT));
        if (q < 0.32)
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.75, 0.88, dotT2);
        else if (notch)
        {
            // En notch el compacto se apiña al entrar en expandido.
            double pScale = Lerp(1, 0.92, Smooth01(p));
            CompactScale.ScaleX *= pScale;
            CompactScale.ScaleY *= pScale;
        }

        // El contenido también florece desde el centro durante el reveal:
        // diverge(0) = apiñado al centro, diverge(1) = en su sitio.
        double diverge = Math.Pow(stretchT2, 1.25);
        CompactArtTranslate.X = Lerp(42, 0, diverge);
        CompactTitleTranslate.X = Lerp(6, 0, diverge);
        CompactEqTranslate.X = Lerp(-36, 0, diverge);
        CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = Lerp(0.92, 1, diverge);
        double titleOp = Smooth01(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
        double eqOp = Smooth01(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
        CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
        CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
        CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? Smooth01(dotT2) : 1);

        // En inactivo no hay contenido, pero TAMPOCO se recorta de golpe: la
        // vista anterior se desvanece con el mismo reloj que el ancho de la
        // pieza y solo se retira del árbol al terminar (FinishInactive). El
        // fondo (portada difuminada) es contenido y se apaga igual (001 MOD RF-11, RF-16).
        BackgroundCanvas.Opacity = contentOp;
        bool bgWanted = contentOp > 0.01;
        if ((BackgroundCanvas.Visibility == Visibility.Visible) != bgWanted)
            BackgroundCanvas.Visibility = bgWanted ? Visibility.Visible : Visibility.Collapsed;
        // El hit-test solo se apaga cuando la pieza ya domina la vista: durante
        // la transición el clic sigue llegando al contenido de partida.
        bool contentLive = contentOp > 0.5;
        CompactLayer.IsHitTestVisible = contentLive && p < 0.6 && q > 0.35;
        ExpandedLayer.Opacity = expandedOp * contentOp * (notch ? q : 1);
        ExpandedLayer.IsHitTestVisible = contentLive && p > 0.4 && q > 0.4;

        double artS = Lerp(0.88, 1, Smooth01(Math.Clamp((p - 0.05) / 0.95, 0, 1)));
        // Pop suma un leve bump al arte/título en cambio de pista
        artS += pop * 0.06;
        ExpandedArtScale.ScaleX = ExpandedArtScale.ScaleY = artS;

        double titleS = Lerp(0.90, 1, Smooth01(Math.Clamp((p - 0.08) / 0.9, 0, 1))) + pop * 0.05;
        SongTitleScale.ScaleX = SongTitleScale.ScaleY = titleS;
        SongTitle.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));
        SongTitleTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));

        SongArtist.Opacity = Lerp(0, _islandArtistOpacity, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));
        SongArtistTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));

        ExpandedEq.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.18) / 0.6, 0, 1)));
        SeekRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        SeekTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        ControlsRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
        ControlsTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
    }

    /// <summary>
    /// Pulso breve de arte/título al cambiar de pista (solo en compacto visible
    /// con las animaciones activadas): un acento, nunca un estado nuevo.
    /// </summary>
    private void PlayTrackPop()
    {
        if (!AnimationsEnabled || _expanded) return;
        if (!IsBoxShown || _q < 0.6) return;
        if (_popPlaying) return;
        _pop = 0; _popV = 0; _popPhase = 0; _popPlaying = true;
        EnsureLoop();
    }

    // ------------------------------------------------------------------
    // Geometría del contenedor (clip por frame, sin Storyboards)
    // ------------------------------------------------------------------

    private static Geometry CreateIslandClip(double width, double height, CornerRadius radius)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double tl = Math.Clamp(radius.TopLeft, 0, Math.Min(width, height) / 2);
        double tr = Math.Clamp(radius.TopRight, 0, Math.Min(width, height) / 2);
        double br = Math.Clamp(radius.BottomRight, 0, Math.Min(width, height) / 2);
        double bl = Math.Clamp(radius.BottomLeft, 0, Math.Min(width, height) / 2);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(tl, 0), true, true);
            context.LineTo(new Point(width - tr, 0), true, false);
            AddCorner(context, new Point(width, tr), tr);
            context.LineTo(new Point(width, height - br), true, false);
            AddCorner(context, new Point(width - br, height), br);
            context.LineTo(new Point(bl, height), true, false);
            AddCorner(context, new Point(0, height - bl), bl);
            context.LineTo(new Point(0, tl), true, false);
            AddCorner(context, new Point(tl, 0), tl);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void AddCorner(StreamGeometryContext context, Point end, double radius)
    {
        if (radius <= 0.01)
            context.LineTo(end, true, false);
        else
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
    }

    // ponytail: notch pegado al borde con cueva (W incluye 2*reach de orejas).
    // reach = ancho horizontal (fijo: fillet expandido), drop = caída vertical (morph).
    private static Geometry CreateNotchClip(double width, double height, double bottomRadius, double reach, double drop)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double br = Math.Clamp(bottomRadius, 0, Math.Min(width, height) / 2);
        double r = Math.Clamp(reach, 0, 24);
        double f = Math.Min(Math.Clamp(drop, 0, 20), Math.Max(0, height - br - 1));
        if (Math.Max(r, f) < 0.5)
            return CreateIslandClip(width, height, new CornerRadius(0, 0, br, br));
        double kx = 0.5523 * r, ky = 0.5523 * f; // aprox. cuarto de elipse
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), true, true);
            context.LineTo(new Point(width, 0), true, false);
            context.BezierTo(new Point(width - kx, 0), new Point(width - r, f - ky), new Point(width - r, f), true, false);
            context.LineTo(new Point(width - r, height - br), true, false);
            AddCorner(context, new Point(width - r - br, height), br);
            context.LineTo(new Point(r + br, height), true, false);
            AddCorner(context, new Point(r, height - br), br);
            context.LineTo(new Point(r, f), true, false);
            context.BezierTo(new Point(r, f - ky), new Point(kx, 0), new Point(0, 0), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void ApplyIslandClip(double width, double height, CornerRadius radius)
    {
        IslandBox.Clip = CreateIslandClip(width, height, radius);
    }
}
