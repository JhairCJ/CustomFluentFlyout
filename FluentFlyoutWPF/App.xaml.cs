// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyoutWPF.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows;

namespace FluentFlyoutWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // log unhandled exceptions before crashing
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(args.ExceptionObject as Exception, "Unhandled exception occurred");
            NLog.LogManager.Flush(); // Ensure logs are written before application dies
            // Y se devuelve el OSD nativo de Windows a explorer: morir con el OSD
            // escondido dejaba el shell con una ventana suya transparente y en
            // -99999, que es justo lo que se ve como «explorer roto».
            try { VolumeMixerWindow.ShowVolumeOsd(); } catch { }
        };

        // Antes de nada más: si la sesión anterior murió —un fallo nativo del motor
        // CUDA, por ejemplo— con el OSD de volumen de explorer escondido, su ventana se
        // quedó transparente y fuera de pantalla, que es lo que se ve como «se me ha
        // roto la barra de tareas». Se le devuelve su estilo y su sitio.
        try { VolumeMixerWindow.RepairVolumeOsdFromPreviousSession(); } catch { }

        // Register AUMID for toast notifications
        ToastNotificationManagerCompat.OnActivated += Notifications.HandleNotificationActivation;

        FluentFlyoutWPF.Classes.Dictation.WhisperCudaRuntime.CompletePendingRemoval();
        base.OnStartup(e);
    }
}