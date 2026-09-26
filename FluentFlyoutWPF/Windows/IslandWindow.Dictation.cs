// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Dictado por voz en el Island (spec 006): la tarjeta que se ve mientras se mantiene la
/// tecla —micrófono a la izquierda, ondas del micrófono real a la derecha— y las tres
/// transiciones que la acompañan (grabar, transcribir, retirarse).
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Mientras se dicta, la vista es suya</b>: la funcionalidad sostiene la vista y
/// declara acceso exclusivo, así ningún cambio de canción ni aviso le quita la tarjeta a
/// mitad de frase y el plazo del aviso temporal no la esconde.</item>
/// <item><b>Las ondas son el micrófono, no el sistema</b>: el nivel llega del propio
/// <see cref="DictationService"/> (RMS del último bloque capturado) y se pinta en barras
/// pequeñas; al callar se aplanan, al hablar saltan (RF-2).</item>
/// <item><b>Al terminar se retira sola</b>: escrito el texto, cancelado o con error, el
/// Island vuelve a lo que correspondía —activa vigente o reposo—, sin dejar la tarjeta
/// pegada (RF-9).</item>
/// </list>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Número de barras del visualizador: ancho / paso (3 + 2).</summary>
    private const int DictationBarCount = 12;
    /// <summary>Alto mínimo y máximo de una barra, dentro de los 22 px de la zona.</summary>
    private const double DictationBarMin = 4;
    private const double DictationBarMax = 20;
    /// <summary>Cadencia del visualizador: 20 fps bastan para una onda suave y cuestan poco.</summary>
    private static readonly TimeSpan DictationBarInterval = TimeSpan.FromMilliseconds(50);

    private static readonly Brush DictationListeningBrush = Frozen(Color.FromRgb(0xFF, 0x8A, 0x8A));
    private static readonly Brush DictationIdleBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private readonly Border[] _dictationBars = new Border[DictationBarCount];
    private readonly double[] _dictationLevels = new double[DictationBarCount];
    private DispatcherTimer? _dictationBarsTimer;
    private DictationService? _dictation;
    /// <summary>La tarjeta del dictado es la vista de delante ahora mismo.</summary>
    private bool _dictationViewShown;

    /// <summary>Funcionalidad «dictado» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? DictationFeature => FeatureById(IslandFeatureIds.Dictation);

    /// <summary>¿La funcionalidad está encendida con el contenedor?</summary>
    private bool DictationModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.DictationEnabled;

    /// <summary>¿Hay una sesión de dictado en marcha? Es lo que sostiene la vista y da acceso exclusivo.</summary>
    private bool DictationActive() => _dictation?.Active == true;

    internal IslandFeatureState GetDictationFeatureState()
    {
        bool enabled = DictationModeAvailable();
        bool active = DictationActive();
        return new IslandFeatureState(enabled, enabled, active,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Dictation,
            // Con una sesión en marcha la tarjeta es exclusiva: ni un cambio de canción,
            // ni un aviso, ni el puntero la sustituyen hasta que el dictado termina.
            Exclusive: active);
    }

    internal bool ShowDictationCompactFromContract()
    {
        if (!DictationModeAvailable() || _dictation == null) return false;
        ShowDictationCompact();
        return true;
    }

    /// <summary>
    /// Vincula el servicio de dictado —vive en MainWindow, que es quien tiene el gancho de
    /// teclado— y crea las barras del visualizador, que no pueden venir del XAML porque su
    /// número y su alto los manda el nivel capturado.
    /// </summary>
    private void InitDictation()
    {
        BuildDictationBars();
        _dictation = _main.Dictation;
        _dictation.Changed += OnDictationChanged;
    }

    private void ShutdownDictation()
    {
        if (_dictation != null) _dictation.Changed -= OnDictationChanged;
        _dictation = null;
        StopDictationBars();
    }

    /// <summary>Ajuste en caliente: el dictado se apagó con su tarjeta delante.</summary>
    public void RefreshDictationContent() => Dispatcher.Invoke(() =>
    {
        if (!DictationModeAvailable() && _dictationViewShown)
        {
            _dictationViewShown = false;
            StopDictationBars();
            FallbackFromDictationView();
        }
        ApplyContentVisibility();
        SyncMeasuredHeight();
        UpdateArrows();
    });

    // ------------------------------------------------------------------
    // Ciclo de la sesión
    // ------------------------------------------------------------------

    /// <summary>El servicio avisa desde su propio hilo (el de la transcripción): al de UI.</summary>
    private void OnDictationChanged()
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(new Action(SyncDictationView));
    }

    /// <summary>
    /// Punto único de la tarjeta: sesión en marcha la presenta, terminada la retira. El
    /// error se enseña —con su motivo— hasta que el servicio lo olvida.
    /// </summary>
    private void SyncDictationView()
    {
        var dictation = _dictation;
        if (_disposed || dictation == null) return;

        if (dictation.Active)
        {
            ShowDictationCompact();
            return;
        }
        // El fallo se presenta aunque la tarjeta no estuviera a la vista (falta el modelo,
        // no hay micrófono): el motivo es justo lo que el usuario necesita leer, y sin esta
        // rama el dictado se quedaba mudo (RF-6).
        if (dictation.Phase == DictationPhase.Error)
        {
            ShowDictationCompact();
            return;
        }
        if (!_dictationViewShown) return;

        // Idle: la tarjeta se retira y el Island vuelve a lo suyo (RF-9).
        _dictationViewShown = false;
        StopDictationBars();
        FallbackFromDictationView();
    }

    /// <summary>
    /// Presenta la tarjeta. AVISO: no tiene expandido ni se navega hasta ella, así que vive
    /// fuera de las pantallas (como el de Bluetooth y el del cargador). Con la sesión en
    /// marcha la funcionalidad es exclusiva y la política del aviso no arma plazo: la
    /// tarjeta dura lo que dure el dictado, no lo que dure un aviso.
    /// </summary>
    private void ShowDictationCompact()
    {
        if (!DictationModeAvailable() || _dictation == null) return;
        _dictationViewShown = true;
        // Las ondas solo corren con micro abierto: un aviso de fallo no anima nada.
        if (DictationActive()) StartDictationBars(); else StopDictationBars();
        RefreshDictationUI();
        ShowCompactView(IslandContentMode.Dictation, DictationFeature, RefreshDictationUI,
            forceNotice: true, restartNotice: true);
    }

    /// <summary>
    /// La tarjeta del dictado dejó de ser la vista de delante: se repliega a la activa
    /// vigente o al reposo, sin dejar la superficie del aviso (mismo camino que el cargador).
    /// </summary>
    private void FallbackFromDictationView()
    {
        if (_noticeForced) ClearTemporaryNotice();
        if (RecoverScreensAfterMemberLost()) return;
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        HidePerMode();
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    /// <summary>
    /// Pinta el estado: micrófono rojo y sin texto mientras graba (el centro es para lo que
    /// hay que LEER), y el mensaje —transcribiendo o el motivo del fallo— cuando lo hay.
    /// </summary>
    private void RefreshDictationUI()
    {
        var dictation = _dictation;
        if (dictation == null) return;

        bool listening = dictation.Phase == DictationPhase.Listening;
        // Rojo mientras escucha; el micro tachado se reserva para el fallo (no para
        // «transcribiendo», que sería leerlo como «te estoy ignorando»).
        DictationGlyph.Foreground = listening ? DictationListeningBrush : DictationIdleBrush;
        DictationGlyph.Symbol = dictation.Phase == DictationPhase.Error
            ? Wpf.Ui.Controls.SymbolRegular.MicOff24
            : Wpf.Ui.Controls.SymbolRegular.Mic24;

        DictationStatus.Text = dictation.Phase switch
        {
            DictationPhase.Transcribing => IslandStrings.Get("IslandDictationTranscribing", "Transcribing…"),
            DictationPhase.Error => IslandStrings.Get(dictation.MessageKey ?? "", dictation.MessageFallback ?? ""),
            _ => "",
        };
        DictationCompactGrid.ToolTip = DictationTooltip(dictation);
    }

    /// <summary>Rótulo al pasar por encima: qué atajo se mantiene y con qué modelo se dicta.</summary>
    private string DictationTooltip(DictationService dictation)
    {
        string hotkey = DictationHotkey.IsValid(SettingsManager.Current.DictationHotkey)
            ? SettingsManager.Current.DictationHotkey
            : DictationHotkey.Default;
        return dictation.Phase switch
        {
            DictationPhase.Listening => IslandStrings.Format("IslandDictationTooltipListening",
                "Listening… hold {0} to dictate", hotkey),
            DictationPhase.Transcribing => IslandStrings.Get("IslandDictationTranscribing", "Transcribing…"),
            DictationPhase.Error => IslandStrings.Get(dictation.MessageKey ?? "", dictation.MessageFallback ?? ""),
            _ => IslandStrings.Format("IslandTooltipDictation", "Dictate by holding {0}", hotkey),
        };
    }

    private void BuildDictationBars()
    {
        DictationWave.Children.Clear();
        for (int i = 0; i < _dictationBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = DictationBarMin,
                CornerRadius = new CornerRadius(1.5),
                Background = DictationListeningBrush,
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _dictationBars[i] = bar;
            DictationWave.Children.Add(bar);
        }
    }

    private void StartDictationBars()
    {
        if (_dictationBarsTimer == null)
        {
            _dictationBarsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = DictationBarInterval };
            _dictationBarsTimer.Tick += OnDictationBarsTick;
        }
        if (!_dictationBarsTimer.IsEnabled) _dictationBarsTimer.Start();
    }

    private void StopDictationBars()
    {
        _dictationBarsTimer?.Stop();
        Array.Clear(_dictationLevels);
        foreach (var bar in _dictationBars)
        {
            if (bar != null) bar.Height = DictationBarMin;
        }
    }

    /// <summary>
    /// La onda entra por la derecha (el dato nuevo siempre es el borde): las barras corren
    /// una posición y la última recibe el nivel de ahora, con caída suave para que el
    /// silencio no corte en seco (RF-2).
    /// </summary>
    private void OnDictationBarsTick(object? sender, EventArgs e)
    {
        var dictation = _dictation;
        if (dictation == null || !dictation.Active) return;

        for (int i = 0; i < _dictationLevels.Length - 1; i++)
        {
            _dictationLevels[i] = _dictationLevels[i + 1];
        }
        // El nivel crudo salta mucho entre bloques; se queda lo alto para que la onda respire.
        double incoming = Math.Clamp(dictation.Level, 0, 1);
        _dictationLevels[^1] = Math.Max(incoming, _dictationLevels[^1] * 0.6);

        for (int i = 0; i < _dictationBars.Length; i++)
        {
            double value = _dictationLevels[i];
            double height = DictationBarMin + Math.Pow(value, 0.7) * (DictationBarMax - DictationBarMin);
            _dictationBars[i].Height = height;
        }
    }
}
