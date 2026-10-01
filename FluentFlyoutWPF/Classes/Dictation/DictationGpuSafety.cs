// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Interruptor de seguridad del motor CUDA del dictado.
///
/// <para>Cargar CUDA dentro del proceso es la maniobra más frágil de toda la app: la
/// avería típica —un controlador NVIDIA instalado a medias— revienta (c0000005) o se
/// cuelga DENTRO de la DLL nativa, sin excepción gestionada que contener: ningún
/// try/catch la intercepta. Cuando eso pasa, el proceso muere con el flyout abierto
/// y sin nadie que devuelva al shell lo que se le hubiera tomado prestado.</para>
///
/// <para>Como el fallo no se puede contener, la única defensa es no repetirlo: antes
/// de cargar CUDA se deja un aviso en disco y solo se borra cuando la carga termina
/// bien y la primera inferencia sale adelante (o cuando la app cierra ordenadamente).
/// Si el proceso muere de forma sucia con el aviso puesto, el siguiente arranque lo
/// lee, apaga la aceleración y dicta en CPU: el usuario puede volver a activarla
/// cuando arregle el controlador.</para>
/// </summary>
internal static class DictationGpuSafety
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object Gate = new();

    private static bool _previousCrashChecked;
    private static bool _previousCrash;
    private static bool _armed;

    /// <summary>Aviso que deja la sesión viva para la siguiente (junto a los ajustes).</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "dictation-gpu.pending");

    /// <summary>
    /// ¿El arranque anterior murió con una carga de CUDA a medias? Se lee una sola vez
    /// en toda la sesión y consume el aviso: la respuesta ya no cambia.
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
    /// Deja el aviso en disco antes de tocar el runtime nativo. Se escribe en un
    /// archivo aparte y no dentro de los ajustes a propósito: si el proceso muere a
    /// mitad de la carga, el disco tiene que contar lo que pasó sin depender de que
    /// nadie llegue a guardar nada más.
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
    /// Retira el aviso: la carga terminó bien y el motor ya está en marcha (o la app
    /// cierra ordenadamente, que no es un fallo).
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
