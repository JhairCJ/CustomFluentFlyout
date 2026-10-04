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
/// Apariencia del Island: estilo (pill/notch), radios, tipografía, línea de
/// actividad, punto de estado, botón del ecualizador y posición en pantalla.
///
/// <para>Todo lo que no es geometría animada (eso vive en
/// <c>IslandWindow.Frame.cs</c>) ni contenido musical (eso, en
/// <c>IslandWindow.Media.cs</c>). Cada propiedad lee su ajuste con un valor
/// por defecto seguro, de modo que un ajuste ausente o corrupto no rompe el
/// render.</para>
/// </summary>
public partial class IslandWindow
{
    private bool IsNotch => Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1) == 1;

    // Radios independientes de compacto y expandido (0-40). El <0 es "sin migrar":
    // hereda el radio único heredado hasta que CompleteInitialization lo rellena.
    private double IslandCompactRadius => Math.Clamp(
        SettingsManager.Current.IslandCompactBorderRadius < 0
            ? SettingsManager.Current.IslandBorderRadius
            : SettingsManager.Current.IslandCompactBorderRadius, 0, 40);
    private double IslandExpandedRadius => Math.Clamp(
        SettingsManager.Current.IslandExpandedBorderRadius < 0
            ? SettingsManager.Current.IslandBorderRadius
            : SettingsManager.Current.IslandExpandedBorderRadius, 0, 40);

    public void RefreshAppearance()
    {
        ApplyStyle();
        ApplyAlbumArtRadius();
        ApplyIslandTextStyle();
        UpdateLine();
        ApplyFrame();
        UpdateDictationShimmer();
    }

    /// <summary>
    /// El idioma de la app cambió en caliente. Los textos del XAML se refrescan solos
    /// (<c>{DynamicResource}</c>); aquí se re-aplican los que escribe el código
    /// (estado del temporizador, del calendario y del cargador) para que el Island no
    /// quede a medio traducir.
    /// </summary>
    private void OnLanguageChanged() => Dispatcher.Invoke(() =>
    {
        if (_disposed) return;
        ApplyIslandTextStyle();
        UpdateLine();
        if (!IsBoxShown) return;
        if (_contentMode == IslandContentMode.Timer) RefreshTimerUI();
        else if (_contentMode == IslandContentMode.Calendar) RefreshCalendarList();
        else if (_contentMode == IslandContentMode.Power) RefreshPowerUI();
        else if (_contentMode == IslandContentMode.Dictation) RefreshDictationUI();
    });

    private int _appliedStyle = -1;

    /// <summary>Estilo del contenedor (pill o notch) y borde según ajuste.</summary>
    private void ApplyStyle()
    {
        int style = Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1);
        if (style != _appliedStyle)
        {
            _appliedStyle = style;
            if (style == 1)
            {
                IslandBox.BorderThickness = new Thickness(1, 0, 1, 1);
                CompactLayer.Width = RestCompactWidth(NotchCompactWidth);
            }
            else
            {
                IslandBox.BorderThickness = new Thickness(1);
                CompactLayer.Width = ContentCompactWidth;
            }
        }

        // Mientras se arrastra algo sobre el Island (estante) el borde se ilumina aunque
        // el borde configurado esté apagado: es la señal de que ahí se puede soltar, no
        // decoración.
        IslandBox.BorderBrush = _shelfDropHot
            ? ShelfDropBrush
            : SettingsManager.Current.IslandBorderEnabled ? IslandBorderBrush : Brushes.Transparent;

        // CornerRadius lo gobierna ApplyFrame por frame (punto 26→cápsula)
        SyncMeasuredHeight();
        if (!_loopOn) ApplyFrame();
    }

    // --- tipografía ---

    // Tipografía del Island (misma resolución que el widget: incluidas por pack URI,
    // resto como fuente del sistema). Una sola familia compartida por los 3 textos.
    private static FontFamily IslandFontFamily =>
        WidgetFonts.Resolve(SettingsManager.Current.IslandFontFamily);

    private static int IslandCompactTitleSize =>
        Math.Clamp(SettingsManager.Current.IslandCompactTitleFontSize, 10, 24);

    private static int IslandExpandedTitleSize =>
        Math.Clamp(SettingsManager.Current.IslandExpandedTitleFontSize, 10, 24);

    private static int IslandExpandedArtistSize =>
        Math.Clamp(SettingsManager.Current.IslandExpandedArtistFontSize, 10, 24);

    // Mismo mapping de preset que el widget (0 Moderno, 1 Clásico, 2 Audaz, 3 Suave).
    private static int IslandTitleWeight => SettingsManager.Current.IslandTextStyle switch
    {
        1 => 400,
        2 => 700,
        3 => 500,
        _ => 600,
    };

    private static int IslandArtistWeight =>
        SettingsManager.Current.IslandTextStyle == 2 ? 600 : 400;

    private static double IslandArtistOpacity => SettingsManager.Current.IslandTextStyle switch
    {
        1 => 0.5,
        2 => 0.85,
        3 => 0.6,
        _ => 0.65,
    };

    private static bool IslandArtistItalic =>
        SettingsManager.Current.IslandTextStyle == 3;

    private double _islandArtistOpacity = 0.65;

    private static FontWeight ToIslandFontWeight(int weight) => weight switch
    {
        >= 700 => FontWeights.Bold,
        >= 600 => FontWeights.SemiBold,
        >= 500 => FontWeights.Medium,
        _ => FontWeights.Normal,
    };

    /// <summary>
    /// Aplica la tipografía del Island (familia compartida, preset de estilo y los
    /// 3 tamaños: canción compacta, canción expandida, autor expandido). Se llama
    /// al arrancar y en cada <see cref="RefreshAppearance"/>; re-mide la altura
    /// expandida porque depende de la fuente.
    /// </summary>
    public void ApplyIslandTextStyle()
    {
        FontFamily family = IslandFontFamily;

        CompactTitle.FontFamily = family;
        SongTitle.FontFamily = family;
        SongArtist.FontFamily = family;

        CompactTitle.FontSize = IslandCompactTitleSize;
        SongTitle.FontSize = IslandExpandedTitleSize;
        SongArtist.FontSize = IslandExpandedArtistSize;

        FontWeight titleWeight = ToIslandFontWeight(IslandTitleWeight);
        CompactTitle.FontWeight = titleWeight;
        SongTitle.FontWeight = titleWeight;
        SongArtist.FontWeight = ToIslandFontWeight(IslandArtistWeight);
        SongArtist.FontStyle = IslandArtistItalic ? FontStyles.Italic : FontStyles.Normal;
        _islandArtistOpacity = IslandArtistOpacity;

        SyncMeasuredHeight();
    }

    /// <summary>Radio de la carátula en compacto y expandido (con su recorte circular).</summary>
    private void ApplyAlbumArtRadius()
    {
        double radius = Math.Clamp(SettingsManager.Current.IslandAlbumArtRadius, 0, 32);
        double compactRadius = Math.Min(radius, 11);
        double expandedRadius = Math.Min(radius, 32);
        var compactCorners = new CornerRadius(compactRadius);
        var expandedCorners = new CornerRadius(expandedRadius);

        CompactArtWrap.CornerRadius = compactCorners;
        CompactAlbumOverlay.CornerRadius = compactCorners;
        CompactArtWrap.Clip = CreateAlbumArtClip(22, compactRadius);
        CompactAlbumOverlay.Clip = CreateAlbumArtClip(22, compactRadius);

        ExpandedArtWrap.CornerRadius = expandedCorners;
        ExpandedAlbumOverlay.CornerRadius = expandedCorners;
        ExpandedArtWrap.Clip = CreateAlbumArtClip(48, expandedRadius);
        ExpandedAlbumOverlay.Clip = CreateAlbumArtClip(48, expandedRadius);
    }

    private static RectangleGeometry CreateAlbumArtClip(double size, double radius)
    {
        var clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        clip.Freeze();
        return clip;
    }

    // --- línea de actividad y punto de estado ---

    /// <summary>
    /// Línea de actividad y márgenes del notch/pieza según los offsets configurados.
    /// Aquí se resuelven las DOS caras de lo mismo (<see cref="IslandLine"/>): la MANIJA
    /// —si la línea y su punto se pintan— y la PUERTA —si la franja del borde sigue
    /// viva—, que comparten offset y presencia. Con la puerta cerrada el puntero no hace
    /// nada en esa zona: ni micro-crecimiento, ni apertura por clic, ni hook de ratón.
    /// </summary>
    private void UpdateLine()
    {
        ApplyStyle();
        // La línea y la isla salen de la MISMA regla pura (IslandLine): el offset con
        // el que se pinta la línea es el que usa el puntero para su franja, así que las
        // dos ya no pueden desincronizarse. En notch la línea es parte del borde; en
        // flotante la baja el ajuste.
        double lineOff = IslandLine.TopDip(IsNotch, SettingsManager.Current.IslandLineTopOffset);
        int islandOff = IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80);
        _lineTopDip = lineOff;
        // La manija existe si hay puerta: alguna cosa usable que el clic pueda abrir.
        _lineShown = IslandLine.Shown(SettingsManager.Current.IslandActivityLine, AnyScreenUsable());
        ActivityLine.Height = IslandLine.BarHeight;
        ActivityLine.Margin = new Thickness(0, lineOff, 0, 0);
        MediaStatusDot.Margin = new Thickness(0, lineOff, 0, 0);
        IslandBox.Margin = new Thickness(0, islandOff, 0, 0);
        // La franja cubre del borde superior de la ventana (donde arranca el Island) a
        // la línea: es la zona de la manija, así que su alto sale del MISMO offset.
        HoverStrip.Margin = new Thickness(0);
        HoverStrip.Height = lineOff + IslandLine.BarHeight + HoverTolV;
        HoverStrip.Width = IslandLine.BarWidth + 2 * HoverTolH;
        HoverStrip.HorizontalAlignment = HorizontalAlignment.Center;
        HoverStrip.VerticalAlignment = VerticalAlignment.Top;
        ApplyAccessZone();
        var eqVis = SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
        ExpandedEq.Visibility = eqVis;
        UpdateEqButton(); // arbitra CompactEq vs icono de pausa
        UpdateMediaStatusDot();
        // La línea la pinta el motor por frame; sin motor hay que pintarla aquí, que su
        // presencia (la manija) acaba de cambiar.
        if (!_loopOn) ApplyFrame();
        // La puerta la acaba de resolver ApplyAccessZone: el hook del ratón solo debe
        // existir mientras haya puerta viva, y este es el punto donde eso cambia.
        SyncPointerHook();
    }

    /// <summary>
    /// La franja del borde es la PUERTA del Island: existe donde hay algo visible a lo
    /// que apuntar —la caja o su línea— y, si el usuario lo pide, también sin nada
    /// dibujado (<c>IslandHiddenAccess</c>). Con la puerta cerrada el puntero ni la ve:
    /// ni micro-crecimiento, ni apertura por clic, ni disparo del hook. El dictado la
    /// veta siempre (RF-10).
    /// </summary>
    private void ApplyAccessZone()
    {
        bool zone = AccessZoneActive();
        HoverStrip.Visibility = zone ? Visibility.Visible : Visibility.Collapsed;
        HoverStrip.IsHitTestVisible = zone;
    }

    /// <summary>
    /// ¿La franja del borde está viva ahora mismo? La consulta el pintado y también el
    /// hook del ratón, que corre en cada movimiento: solo lee estado cacheado.
    /// </summary>
    private bool AccessZoneActive() =>
        !DictationActive()
        && IslandLine.AccessZone(IsBoxShown, _lineShown, SettingsManager.Current.IslandHiddenAccess);

    /// <summary>El punto de estado solo con contenido visible y sin pieza inactiva.</summary>
    private void UpdateMediaStatusDot()
    {
        // El punto vive en la MISMA manija que la línea: si el indicador no se pinta
        // —ajuste apagado, sin puerta o caja ocupando su sitio— tampoco hay punto.
        // Durante la transición a inactivo sigue visible y se apaga por opacidad
        // (ApplyFrame); solo se retira al llegar del todo a la pieza.
        if (!_lineShown || _inactiveT >= 1)
        {
            MediaStatusDot.Visibility = Visibility.Collapsed;
            return;
        }
        bool hidden = Visibility == Visibility.Visible && !IsBoxShown && !Suppressed();
        // Punto de estado estrictamente multimedia (001 MOD RF-20): solo aparece
        // con un snapshot musical real (reproducción o pausa); con solo el
        // temporizador disponible permanece oculto, sin consultar el control multimedia.
        var status = _music?.Status;
        if (!hidden || status == null)
        {
            MediaStatusDot.Visibility = Visibility.Collapsed;
            return;
        }

        MediaStatusDot.Background = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? MediaPlayingBrush
            : MediaPausedBrush;
        MediaStatusDot.Visibility = Visibility.Visible;
    }

    // En compacto manda el visualizador; en pausa lo reemplaza el icono
    // (clic al icono = reanudar + vuelve el visualizador).
    private void UpdateEqButton()
    {
        var s = AnySession();
        bool zone = !_expanded && SettingsManager.Current.IslandEqEnabled && s != null;
        bool paused = s != null && zone && SafeStatus(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        EqPlayPauseBtn.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = paused ? Visibility.Collapsed
            : SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EqZone_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PlayPause_Click(sender, e);
        // Flip optimista de vista; UpdateLine/Tick lo confirman.
        bool toIcon = EqPlayPauseBtn.Visibility != Visibility.Visible;
        EqPlayPauseBtn.Visibility = toIcon ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsManager.Current.IslandEqEnabled)
            CompactEq.Visibility = toIcon ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- posición ---

    /// <summary>
    /// Centra la ventana en la parte superior del monitor principal. Usa la
    /// instantánea de contexto cacheada (001 MOD RF-12): la geometría del monitor
    /// solo se relee cuando Windows avisa de un cambio.
    ///
    /// <para>La ventana es el lienzo transparente del Island: si el contenido vigente
    /// pide más ancho que el de siempre —una pantalla combinada con sus columnas
    /// (change island-pantallas RF-3)— se ensancha ANTES de centrar, porque WPF recorta
    /// lo que sobresale de la ventana. Lo transparente sigue siendo click-through, así
    /// que ensanchar no le quita el ratón a nadie.</para>
    /// </summary>
    private void PositionTopCenter()
    {
        var primary = PrimaryMonitor();
        if (primary.monitorArea.Width == 0) return;
        double content = _expanded || _p > 0.05 ? ContentExpandedWidth : ContentCompactWidth;
        // El margen deja sitio a las flechas laterales del expandido.
        double monitorWidth = primary.dpiX > 0
            ? primary.workArea.Width * 96.0 / primary.dpiX
            : DefaultWindowWidth;
        double maxWindow = Math.Max(280, monitorWidth - 24);
        double wanted = Math.Min(Math.Max(DefaultWindowWidth, content + 80), maxWindow);
        if (Math.Abs(Width - wanted) > 0.5) Width = wanted;
        double rawW = Width * primary.dpiX / 96.0;
        Left = (primary.workArea.Left + primary.workArea.Width / 2 - rawW / 2) * 96.0 / primary.dpiX;
        Top = primary.workArea.Top * 96.0 / primary.dpiY;
        WindowHelper.SetTopmost(this);
    }
}
