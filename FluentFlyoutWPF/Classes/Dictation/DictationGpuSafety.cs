// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Safety switch for the dictation CUDA engine.
///
/// <para>Loading CUDA inside the process is the most fragile maneuver in the whole
/// app: the typical failure - a half-installed NVIDIA driver - crashes (c0000005) or
/// hangs INSIDE the native DLL, with no managed exception to contain: no try/catch
/// catches it. When that happens the process dies with the flyout open and nobody to
/// give the shell back what it had borrowed.</para>
///
/// <para>Since the failure cannot be contained, the only defense is not repeating it:
/// before loading CUDA a notice is left on disk and it is only deleted when the load
/// finishes well and the first inference gets through (or when the app shuts down
/// cleanly). If the process dies dirty with the notice in place, the next startup
/// reads it, turns acceleration off and dictates on CPU: the user can turn it back on
/// once the driver is fixed.</para>
/// </summary>
internal static class DictationGpuSafety
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object Gate = new();

    private static bool _previousCrashChecked;
    private static bool _previousCrash;
    private static bool _armed;

    /// <summary>Notice a live session leaves for the next one (next to the settings).</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "dictation-gpu.pending");

    /// <summary>
    /// Did the previous startup die with a CUDA load half done? Read once per session and
    /// it consumes the notice: the answer does not change afterwards.
    /// </summary>
    public static bool PreviousLoadCrashed
    {
        get
        {
            lock (Gate)
            {
                if (_previousCrashChecked) return _previousCrash;
                _previousCrashChecked = true;
            }

            try
            {
                string path = MarkerPath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _previousCrash = true;
                    Logger.Warn("El arranque anterior murió cargando el motor CUDA del dictado: "
                        + "esta sesión usará CPU y la aceleración queda desactivada.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "No se pudo leer el aviso de CUDA de la sesión anterior");
            }

            return _previousCrash;
        }
    }

    /// <summary>
    /// Leaves the notice on disk before touching the native runtime. It is written to a
    /// separate file and not into the settings on purpose: if the process dies halfway
    /// through the load, disk has to tell what happened without depending on anyone
    /// getting to save anything else.
    /// </summary>
    public static void Arm()
    {
        lock (Gate)
        {
            if (_armed) return;
            _armed = true;
        }

        try
        {
            string path = MarkerPath;
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, $"{Environment.ProcessId} {DateTime.UtcNow:O}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo dejar el aviso de carga de CUDA en disco");
        }
    }

    /// <summary>
    /// Withdraws the notice: the load finished well and the engine is running (or the
    /// app is closing cleanly, which is not a failure).
    /// </summary>
    public static void Disarm()
    {
        lock (Gate)
        {
            if (!_armed) return;
            _armed = false;
        }

        try
        {
            string path = MarkerPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo retirar el aviso de carga de CUDA");
        }
    }
}
