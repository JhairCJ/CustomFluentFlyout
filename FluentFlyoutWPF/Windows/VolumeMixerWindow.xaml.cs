// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

// Portions of this code are derived from:
// - gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD
//
// Copyright (c) 2022 gpkgpk
// Modifications copyright (c) 2026 The FluentFlyout Authors

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.ViewModels;
using MicaWPF.Controls;
using NLog;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Interaction logic for VolumeMixerWindow.xaml
/// </summary>
public partial class VolumeMixerWindow : MicaWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    public VolumeMixerViewModel ViewModel { get; } = new();
    public UserSettings UserSettings => SettingsManager.Current;

    private static IntPtr _nativeOsdElement = IntPtr.Zero;
    private static int _nativeOsdOriginalExStyle;
    /// <summary>PID del dueño del OSD que se escondió: el OSD nativo es de explorer.</summary>
    private static uint _nativeOsdOwnerPid;
    /// <summary>Rectángulo original del OSD: hay que devolverlo a su sitio, no a (0,0).</summary>
    private static RECT _nativeOsdOriginalRect;
    private static readonly object _osdGate = new();

    /// <summary>
    /// Aviso en disco con lo que se le hizo al OSD nativo. Lo escribe la sesión que lo
    /// esconde y lo consume la siguiente: si este proceso murió sin poder restaurarlo
    /// —un fallo nativo de CUDA, por ejemplo—, la ventana de explorer se quedaría
    /// transparente y fuera de pantalla para siempre.
    /// </summary>
    private static string VolumeOsdMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "volume-osd.pending");
    private CancellationTokenSource _cts;
    private MainWindow _mainWindow;
    private readonly double _collapsedHeight = 50;
    private readonly double _normalWidth;
    private bool _isHiding = true;

    private long _lastFlyoutTime = 0;
    private readonly TimeSpan _flyoutCooldown = TimeSpan.FromMilliseconds(500);

    public VolumeMixerWindow()
    {
        DataContext = this;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);
        CustomWindowChrome.CaptionHeight = 0;
        CustomWindowChrome.UseAeroCaptionButtons = false;
        CustomWindowChrome.GlassFrameThickness = new Thickness(0);

        _mainWindow = (MainWindow)Application.Current.MainWindow;
        _cts = new CancellationTokenSource();
        _normalWidth = Width;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // one day we might want to convert these to an interface
    public async void ShowFlyout()
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning())
            return;

        long currentTime = Environment.TickCount64;

        if (currentTime - _lastFlyoutTime < _flyoutCooldown.TotalMilliseconds)
        {
            return;
        }

        _lastFlyoutTime = currentTime;

        if (_isHiding)
        {
            if (_nativeOsdElement == IntPtr.Zero)
            {
                _ = Task.Run(() =>
                {
                    HideVolumeOsd();
                });
            }

            _isHiding = false;
            if (SettingsManager.Current.VolumeMixerAcrylicWindowEnabled)
            {
                WindowBlurHelper.EnableBlur(this);
            }
            else
            {
                WindowBlurHelper.DisableBlur(this);
            }

            // refresh all data
            ViewModel.OnPollTick(null, EventArgs.Empty);

            bool aboveMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout;
            if (aboveMedia)
            {
                Width = _mainWindow.Width;
                _mainWindow.OpenAnimation(this, aboveReference: _mainWindow);
            }
            else
            {
                Width = _normalWidth;
                _mainWindow.OpenAnimation(this, alwaysBottom: true);
            }

            Show();
            //WindowHelper.SetNoActivate(this);
            WindowHelper.SetTopmost(this);
        }

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token); // check if mouse is over every 100ms
                // update master volume again because it can be slow to update when coming from a hardware key press
                ViewModel.SyncMasterFromDevice();

                bool mouseOverThis = WindowHelper.IsMouseOverWindow(this);
                bool mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout
                    && _mainWindow.Visibility == Visibility.Visible
                    && WindowHelper.IsMouseOverWindow(_mainWindow); // sync with media flyout

                if (!mouseOverThis && !mouseOverMedia)
                {
                    await Task.Delay(SettingsManager.Current.VolumeControlDuration, token);

                    mouseOverThis = WindowHelper.IsMouseOverWindow(this);
                    mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout
                        && _mainWindow.Visibility == Visibility.Visible
                        && WindowHelper.IsMouseOverWindow(_mainWindow);

                    if (!mouseOverThis && !mouseOverMedia)
                    {
                        _mainWindow.CloseAnimation(this);
                        _isHiding = true;
                        await Task.Delay(MainWindow.getDuration());
                        if (_isHiding == false) return;

                        WindowHelper.SetVisibility(this, false);
                        ViewModel.IsExpanded = false;
                        break;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            // do nothing
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VolumeMixerViewModel.IsExpanded))
        {
            AnimateExpandCollapse(ViewModel.IsExpanded);
        }
    }

    // derived from gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD
    //
    // Endurecido. Esta maniobra toca una ventana de OTRO proceso —el OSD de volumen
    // nativo lo hospeda explorer.exe— y hacerlo mal se lleva por delante al dueño:
    //
    //  1) El recorrido era un bucle infinito: <c>FindWindowEx(Zero, Zero, ...)</c>
    //     devuelve SIEMPRE la primera ventana de la clase, así que una candidata
    //     descartada se reintentaba para siempre, con su <c>ShowWindow(SW_RESTORE)</c>
    //     repitiéndose en el mismo hilo que la llamara.
    //  2) No se comprobaba de quién era la ventana: podía coger la isla XAML de otra
    //     aplicación y dejarla invisible y en -99999. A esa aplicación se le rompe la
    //     interfaz y FluentFlyout se queda con un handle que no es suyo.
    //  3) Al restaurar usaba el handle guardado sin comprobar que siguiera siendo la
    //     MISMA ventana: si explorer se reinicia (o Windows recicla el HWND), esos
    //     estilos se le escriben a una ventana ajena. Ese es el camino por el que un
    //     FluentFlyout que muere deja al shell con la interfaz rota.
    private static void HideVolumeOsd()
    {
        IntPtr osd = FindNativeVolumeOsd();
        if (osd == IntPtr.Zero)
        {
            Logger.Warn("OSD window not found.");
            return;
        }

        uint ownerPid;
        lock (_osdGate)
        {
            // Ya escondido: repetir la maniobra guardaría como «original» el estilo
            // retocado y el OSD se quedaría transparente para siempre.
            if (_nativeOsdElement != IntPtr.Zero) return;

            GetWindowProcessId(osd, out ownerPid);
            GetWindowRect(osd, out RECT originalRect);
            _nativeOsdElement = osd;
            _nativeOsdOwnerPid = ownerPid;
            _nativeOsdOriginalExStyle = GetWindowLong(osd, GWL_EXSTYLE);
            _nativeOsdOriginalRect = originalRect;

            // El aviso va a disco ANTES de tocar la ventana ajena: si el proceso muere
            // a mitad, la sesión siguiente sabrá qué ventana, qué estilo y qué sitio
            // devolverle a explorer.
            WriteOsdMarker(osd, ownerPid, _nativeOsdOriginalExStyle, originalRect);

            SetWindowLong(osd, GWL_EXSTYLE,
                _nativeOsdOriginalExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
            SetWindowPos(osd, 0, -99999, -99999, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(osd, SW_MINIMIZE);
        }

        Logger.Info($"Successfully hid volume OSD (hwnd 0x{osd.ToInt64():X}, pid {ownerPid}, rect {_nativeOsdOriginalRect.Left},{_nativeOsdOriginalRect.Top}).");
    }

    /// <summary>
    /// Restaura el OSD nativo. Si el handle guardado ya no es la MISMA ventana —explorer
    /// se reinició y el OSD se recreó, o Windows recicló el HWND para otra ventana— no se
    /// toca nada: la ventana nueva nunca estuvo escondida por nosotros.
    /// </summary>
    public static void ShowVolumeOsd()
    {
        IntPtr osd;
        int originalExStyle;
        uint ownerPid;
        RECT originalRect;
        lock (_osdGate)
        {
            osd = _nativeOsdElement;
            originalExStyle = _nativeOsdOriginalExStyle;
            ownerPid = _nativeOsdOwnerPid;
            originalRect = _nativeOsdOriginalRect;
            _nativeOsdElement = IntPtr.Zero;
        }

        if (osd == IntPtr.Zero)
        {
            Logger.Warn("Did not try to restore OSD because it was either not found or was not hidden.");
            return;
        }

        // En los dos caminos el aviso se consume: si la ventana ya no es la misma, el
        // aviso no sirve para nada y dejarlo solo haría que la próxima sesión volviera
        // a intentarlo en balde.
        ClearOsdMarker();

        if (!IsStillTheSameOsdWindow(osd, ownerPid))
        {
            Logger.Warn("La ventana del OSD cambió mientras estaba escondida: no hay nada que restaurar.");
            return;
        }

        try
        {
            SetWindowLong(osd, GWL_EXSTYLE, originalExStyle);
            // Se le devuelve su sitio exacto: moverla a (0,0) dejaría al OSD —o a lo que
            // se hubiera escondido por error— en una esquina que no era la suya.
            if (originalRect.Right > originalRect.Left && originalRect.Bottom > originalRect.Top)
                SetWindowPos(osd, 0, originalRect.Left, originalRect.Top, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            else
                SetWindowPos(osd, 0, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(osd, SW_RESTORE);
            Logger.Info("Successfully restored volume OSD.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo restaurar el OSD nativo.");
        }
    }

    /// <summary>
    /// Repara al arrancar lo que una sesión anterior pudo dejar a medias: primero el
    /// aviso que dejó escrita (con su ventana, su estilo y su sitio) y después una pasada
    /// por si el aviso se perdió. Se ejecuta antes de que nadie más toque ventanas ajenas.
    /// </summary>
    public static void RepairVolumeOsdFromPreviousSession()
    {
        RestorePendingVolumeOsd();
        RepairOrphanedOsdWindows();
    }

    /// <summary>
    /// Devuelve a explorer el OSD que dejó escondido una sesión anterior que no pudo
    /// limpiar (muerte nativa, o un apagado del que no hubo notificación).
    /// </summary>
    private static void RestorePendingVolumeOsd()
    {
        try
        {
            string path = VolumeOsdMarkerPath;
            if (!File.Exists(path)) return;

            string[] lines = File.ReadAllLines(path);
            // El aviso se consume pase lo que pase: no debe reaparecer en cada arranque.
            ClearOsdMarker();

            if (lines.Length < 4) return;
            if (!long.TryParse(lines[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handle)
                || !uint.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ownerPid)
                || !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int exStyle))
                return;

            string[] rectParts = lines[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rectParts.Length < 4) return;
            if (!int.TryParse(rectParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left)
                || !int.TryParse(rectParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top)
                || !int.TryParse(rectParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int right)
                || !int.TryParse(rectParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bottom))
                return;

            IntPtr osd = (IntPtr)handle;
            if (!IsStillTheSameOsdWindow(osd, ownerPid))
            {
                Logger.Warn("El OSD de la sesión anterior ya no es la misma ventana; no se toca nada.");
                return;
            }

            SetWindowLong(osd, GWL_EXSTYLE, exStyle);
            if (right > left && bottom > top)
                SetWindowPos(osd, 0, left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(osd, SW_RESTORE);
            Logger.Info("OSD nativo devuelto a explorer: la sesión anterior murió con él escondido.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo devolver el OSD nativo de la sesión anterior.");
        }
    }

    /// <summary>
    /// Pasada de reparación para cuando no hay aviso: busca ventanas de explorer con la
    /// firma exacta que deja esta app al esconder el OSD —capa + trasparente a clics +
    /// fuera de pantalla— y les devuelve su estilo y su sitio. Nadie más mueve una ventana
    /// suya a -99999 con esos estilos, así que solo puede estar tocando lo que estropeó
    /// esta app: es lo que convertía un FluentFlyout caído en una barra de tareas rota.
    /// </summary>
    private static void RepairOrphanedOsdWindows()
    {
        try
        {
            for (IntPtr candidate = IntPtr.Zero;
                 (candidate = FindWindowEx(IntPtr.Zero, candidate, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero;)
            {
                if (!IsWindow(candidate) || !IsOwnedByExplorer(candidate)) continue;

                int exStyle = GetWindowLong(candidate, GWL_EXSTYLE);
                if ((exStyle & WS_EX_LAYERED) == 0 || (exStyle & WS_EX_TRANSPARENT) == 0) continue;

                if (!GetWindowRect(candidate, out RECT rect)) continue;
                if (rect.Left > -50_000 || rect.Top > -50_000) continue; // no es nuestra firma

                SetWindowLong(candidate, GWL_EXSTYLE, exStyle & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT));

                if (LooksLikeTaskbar(candidate))
                {
                    // La barra: se le devuelve su franja exacta, que es donde vive su isla XAML.
                    IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
                    if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out RECT bar))
                        SetWindowPos(candidate, 0, bar.Left, bar.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                else
                {
                    SetWindowPos(candidate, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }

                ShowWindow(candidate, SW_RESTORE);
                Logger.Warn($"Ventana de explorer devuelta a su sitio al arrancar (hwnd 0x{candidate.ToInt64():X}): "
                    + "una sesión anterior murió con el OSD escondido.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo reparar el estado del OSD de una sesión anterior");
        }
    }

    /// <summary>Deja por escrito lo que se le hizo al OSD (handle en hexadecimal).</summary>
    private static void WriteOsdMarker(IntPtr osd, uint ownerPid, int exStyle, RECT rect)
    {
        try
        {
            string path = VolumeOsdMarkerPath;
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllLines(path,
            [
                osd.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                ownerPid.ToString(CultureInfo.InvariantCulture),
                exStyle.ToString(CultureInfo.InvariantCulture),
                $"{rect.Left} {rect.Top} {rect.Right} {rect.Bottom}",
            ]);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo dejar el aviso del OSD escondido");
        }
    }

    private static void ClearOsdMarker()
    {
        try
        {
            string path = VolumeOsdMarkerPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo retirar el aviso del OSD");
        }
    }

    /// <summary>
    /// Localiza el OSD nativo avanzando de verdad por las ventanas de su clase y
    /// descartando lo que no sea suyo: solo vale un anfitrión de isla XAML de explorer
    /// (el OSD es suyo) cuya ventana de entrada tenga geometría real.
    /// </summary>
    private static IntPtr FindNativeVolumeOsd()
    {
        for (IntPtr candidate = IntPtr.Zero;
             (candidate = FindWindowEx(IntPtr.Zero, candidate, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero;)
        {
            // Primero el dueño: de una ventana ajena no se toca ni el estado.
            if (!IsOwnedByExplorer(candidate)) continue;

            if (FindWindowEx(candidate, IntPtr.Zero,
                    "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource") == IntPtr.Zero)
                continue;

            IntPtr input = FindWindowEx(candidate, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (input == IntPtr.Zero) continue;

            // El OSD vivo se reconoce porque, restaurada su ventana de entrada, tiene
            // geometría real; los demás anfitriones de islas XAML no la tienen.
            ShowWindow(input, SW_RESTORE);
            if (!GetWindowRect(input, out RECT rect)) continue;
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) continue;

            // La barra de tareas comparte clase con el OSD. Esconderla a ella fue lo que
            // dejaba la barra transparente y sin responder: antes de devolver nada, se
            // descarta todo lo que sea —o viva dentro de— la barra de tareas.
            if (LooksLikeTaskbar(candidate)) continue;

            Logger.Info($"OSD nativo localizado: hwnd 0x{candidate.ToInt64():X}, pid de su dueño {GetOwnerPidOf(candidate)}.");
            return candidate;
        }

        return IntPtr.Zero;
    }

    /// <summary>PID del dueño de una ventana, o 0 si no se puede saber (solo registro).</summary>
    private static uint GetOwnerPidOf(IntPtr hwnd)
    {
        try
        {
            GetWindowProcessId(hwnd, out uint pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// ¿La ventana es el taskbar o vive dentro de él? El taskbar de Windows 11 usa
    /// anfitriones de islas XAML de la misma clase que el OSD, así que sin esta criba la
    /// búsqueda podía devolver la barra entera: se le ponían los estilos del OSD
    /// (transparente, capa, fuera de pantalla) y explorer se quedaba sin barra. Si el
    /// candidato cae dentro del rectángulo del taskbar o es una franja a lo ancho de la
    /// pantalla de su altura, no es el OSD.
    /// </summary>
    private static bool LooksLikeTaskbar(IntPtr candidate)
    {
        try
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                if (candidate == taskbar) return true;

                for (IntPtr parent = GetParent(candidate); parent != IntPtr.Zero; parent = GetParent(parent))
                {
                    if (parent == taskbar) return true;
                }

                if (GetWindowRect(taskbar, out RECT bar) && GetWindowRect(candidate, out RECT candidateRect))
                {
                    if (OverlapRatio(candidateRect, bar) > 0.5) return true;

                    // Misma forma que la barra (franja larga y baja) sin ser exactamente su
                    // rectángulo: las islas XAML del taskbar y de sus widgets.
                    int barWidth = bar.Right - bar.Left;
                    int barHeight = Math.Max(1, bar.Bottom - bar.Top);
                    int candidateWidth = candidateRect.Right - candidateRect.Left;
                    int candidateHeight = candidateRect.Bottom - candidateRect.Top;
                    if (barWidth > 0
                        && candidateWidth >= barWidth * 0.8
                        && candidateHeight <= barHeight * 2)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            // Si no se puede comprobar, se es conservador y no se toca la ventana.
            Logger.Warn(ex, "No se pudo comprobar si la ventana candidata es el taskbar");
            return true;
        }

        return false;
    }

    /// <summary>proporción del rectángulo candidato que cae dentro del rectángulo de referencia.</summary>
    private static double OverlapRatio(RECT candidate, RECT reference)
    {
        int left = Math.Max(candidate.Left, reference.Left);
        int top = Math.Max(candidate.Top, reference.Top);
        int right = Math.Min(candidate.Right, reference.Right);
        int bottom = Math.Min(candidate.Bottom, reference.Bottom);
        long overlap = (long)Math.Max(0, right - left) * Math.Max(0, bottom - top);
        long area = (long)Math.Max(0, candidate.Right - candidate.Left)
            * Math.Max(0, candidate.Bottom - candidate.Top);
        return area <= 0 ? 0 : (double)overlap / area;
    }

    /// <summary>¿La ventana sigue existiendo, con la misma clase y el mismo dueño?</summary>
    private static bool IsStillTheSameOsdWindow(IntPtr hwnd, uint ownerPid)
    {
        try
        {
            if (!IsWindow(hwnd)) return false;
            var className = new System.Text.StringBuilder(64);
            if (GetClassName(hwnd, className, className.Capacity) <= 0) return false;
            if (!className.ToString().Equals("XamlExplorerHostIslandWindow", StringComparison.Ordinal)) return false;
            GetWindowProcessId(hwnd, out uint currentOwner);
            if (ownerPid == 0 || currentOwner != ownerPid) return false;
            // Y el dueño tiene que seguir siendo explorer: si el HWND se recicló para otra
            // aplicación, escribirle estilos de OSD le rompe la interfaz igual que antes.
            return IsOwnedByExplorer(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>¿La ventana es de explorer.exe? (el OSD nativo lo es; la isla de otra app, no)</summary>
    private static bool IsOwnedByExplorer(IntPtr hwnd)
    {
        try
        {
            if (GetWindowProcessId(hwnd, out uint pid) == 0 || pid == 0) return false;
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    if ((uint)process.Id == pid) return true;
                }
            }
        }
        catch
        {
            // Si no se puede saber de quién es, no se toca.
        }

        return false;
    }

    private void AnimateExpandCollapse(bool expand)
    {
        int msDuration = MainWindow.getDuration();
        var easing = msDuration > 0 ? _mainWindow.getEasingStyle(true) : null;
        var duration = new Duration(TimeSpan.FromMilliseconds(msDuration > 0 ? msDuration / 1.4 : 1));

        bool isTop = false;

        // check if the media flyout is at the top or bottom of the screen if applicable
        if (SettingsManager.Current.VolumeControlAboveMediaFlyout)
        {
            isTop = SettingsManager.Current.Position switch
            {
                3 or 4 or 5 => true,
                _ => false
            };
        }

        double expandedHeight;
        if (expand)
        {
            SessionsExpanded.Visibility = Visibility.Visible;
            SessionsSeparator.Visibility = Visibility.Visible;
            SessionsPanel.UpdateLayout();
        }

        // measure desired size
        SessionsExpanded.Measure(new Size(ActualWidth, double.PositiveInfinity));
        expandedHeight = _collapsedHeight + Math.Min(SessionsExpanded.DesiredSize.Height, 220);

        double targetHeight = expand ? expandedHeight : _collapsedHeight;
        double currentHeight = ActualHeight;
        double heightDelta = targetHeight - currentHeight;

        // When at the top, chevron points down (0°) when collapsed and up (180°) when expanded.
        // When at the bottom, chevron points up (180°) when expanded and down (0°) when collapsed.
        var chevronAnimation = new DoubleAnimation
        {
            To = isTop ? (expand ? 0 : 180) : (expand ? 180 : 0),
            Duration = duration,
            EasingFunction = easing
        };
        Dispatcher.Invoke(() =>
        {
            ChevronRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, chevronAnimation);
        });

        var heightAnimation = new DoubleAnimation
        {
            From = currentHeight,
            To = targetHeight,
            Duration = duration,
            EasingFunction = easing
        };

        // When at the top, the window grows downward so Top stays fixed.
        // When at the bottom, the window grows upward so Top shifts up by heightDelta.
        var topAnimation = new DoubleAnimation
        {
            From = Top,
            To = isTop ? Top : Top - heightDelta,
            Duration = duration,
            EasingFunction = easing
        };

        if (!expand)
        {
            heightAnimation.Completed += (s, e) =>
            {
                SessionsExpanded.Visibility = Visibility.Collapsed;
                SessionsSeparator.Visibility = Visibility.Collapsed;
            };
        }

        Dispatcher.Invoke(() =>
        {
            BeginAnimation(TopProperty, topAnimation);
            BeginAnimation(HeightProperty, heightAnimation);
        });
    }
}