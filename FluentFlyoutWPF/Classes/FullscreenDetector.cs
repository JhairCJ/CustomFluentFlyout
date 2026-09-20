// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes;

internal class FullscreenDetector
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Checks if a DirectX exclusive fullscreen application or game is currently running.
    /// </summary>
    /// <returns>
    /// true if a fullscreen DirectX application is running;
    /// false if no fullscreen application is detected, DisableIfFullscreen setting is false, or if the check fails
    /// </returns>
    public static bool IsFullscreenApplicationRunning()
    {
        if (!SettingsManager.Current.DisableIfFullscreen) return false;
        return QueryState() == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
    }

    /// <summary>
    /// ¿El shell está en un estado de pantalla completa (o de equipo ausente)?
    ///
    /// <para>A diferencia de <see cref="IsFullscreenApplicationRunning"/> —que solo
    /// reconoce el D3D EXCLUSIVO y depende del ajuste del Media Flyout—, este
    /// reconoce también <c>QUNS_BUSY</c> (el estado que reportan los juegos y los
    /// vídeos a pantalla completa SIN bordes, que no son D3D exclusivos) y
    /// <c>QUNS_PRESENTATION_MODE</c> (presentaciones), además del equipo bloqueado
    /// (<c>QUNS_NOT_PRESENT</c>). El Island se aparta con él: quedarse encima de un
    /// juego era justo el caso que no se detectaba (001 RF-8/14).</para>
    ///
    /// <para>No mira ningún ajuste: quien llama decide (el Island tiene el suyo).</para>
    /// </summary>
    public static bool IsFullscreenOrAwayState()
    {
        return QueryState() switch
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT => true,
            _ => false,
        };
    }

    /// <summary>
    /// Estado de notificaciones del shell, o null si la consulta falla (nunca
    /// lanza: la detección de pantalla completa no puede tumbar al contenedor).
    /// </summary>
    private static QUERY_USER_NOTIFICATION_STATE? QueryState()
    {
        try
        {
            int result = SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);
            if (result != 0) // 0 means SUCCESS
            {
                throw new Exception($"SHQueryUserNotificationState failed with error code: {result}");
            }
            return state;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error detecting fullscreen state");
            return null;
        }
    }
}