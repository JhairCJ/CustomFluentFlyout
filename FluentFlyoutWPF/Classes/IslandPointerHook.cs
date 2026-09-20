// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Notificación nativa de entrada/salida de la franja del Island (change
/// island-actividad-orientada-eventos, 001 MOD RF-3): hook de ratón de bajo
/// nivel que solo emite un callback cuando el puntero CRUZA la franja, nunca por
/// cada movimiento. Nada de consultas multimedia ni de sondeo continuo; el
/// trabajo del callback es una prueba de rectángulo contra la geometría
/// cacheada.
///
/// <para>El hook se instala en el hilo de UI (que ya tiene bucle de mensajes) y
/// sus callbacks corren en ese mismo hilo, así que el consumidor puede tocar
/// estado de UI sin marshalling. Si la instalación falla, el Island usa el
/// fallback acotado de 250 ms.</para>
/// </summary>
internal sealed class IslandPointerHook : IDisposable
{
    private readonly Func<int, int, bool> _isInside;
    private readonly Action<bool> _onCross;
    // El delegado debe seguir vivo: el hook nativo guarda el puntero a función.
    private LowLevelMouseProc? _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _inside;

    /// <summary>¿El hook nativo quedó instalado? Solo entonces hay notificación real.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    public IslandPointerHook(Func<int, int, bool> isInside, Action<bool> onCross)
    {
        _isInside = isInside;
        _onCross = onCross;
    }

    /// <summary>
    /// Instala el hook. Devuelve false —sin lanzar— si el sistema lo deniega: el
    /// Island decide entonces arrancar el fallback acotado.
    /// </summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _proc = HookProc;
            _hook = SetWindowsHookExMouse(WH_MOUSE_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }
        return IsInstalled;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool inside = _isInside(data.pt.X, data.pt.Y);
                if (inside != _inside)
                {
                    _inside = inside;
                    _onCross(inside);
                }
            }
            catch
            {
                // Un callback del hook jamás puede tumbar el hilo de entrada.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Libera el hook: tras esto no corre ningún callback más (ni al cerrar la
    /// ventana). El estado interior se reinicia para que una reinstalación no
    /// herede un cruce fantasma.
    /// </summary>
    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
        _proc = null;
        _inside = false;
    }
}
