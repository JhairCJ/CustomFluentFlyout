// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Contrato del contenedor escalable (001 MOD RF-11, RF-13): toda funcionalidad
/// declara si está habilitada, disponible, activa y seleccionada, aporta sus
/// vistas compacta y expandida, comunica sus dimensiones preferidas y puede
/// declarar acceso exclusivo persistente (RF-13). El contenedor solo muestra
/// funcionalidades que cumplen el contrato: sin disponibilidad no hay
/// placeholders ni vistas vacías.
/// </summary>
public interface IIslandFeature
{
    /// <summary>Identificador estable de la funcionalidad (p. ej. "media", "timer").</summary>
    string Id { get; }

    /// <summary>Estado del contrato en este instante; el contenedor lo consulta, nunca lo cachea.</summary>
    IslandFeatureState State { get; }

    /// <summary>Ancho preferido de la vista compacta (DIPs).</summary>
    double CompactWidth { get; }

    /// <summary>Alto preferido de la vista compacta (DIPs).</summary>
    double CompactHeight { get; }

    /// <summary>Ancho preferido del expandido (DIPs); 0 = usar el ancho configurado común.</summary>
    double ExpandedPreferredWidth { get; }

    /// <summary>
    /// Alto preferido del expandido (DIPs); 0 = medir el contenido real
    /// (el contenedor mide el expandido y usa esa altura).
    /// </summary>
    double ExpandedPreferredHeight { get; }

    /// <summary>
    /// Muestra el contenido expandido de esta funcionalidad. Devuelve false si
    /// no es usable ahora mismo (el contenedor prueba la siguiente usable y
    /// jamás abre una vista vacía: 001 MOD RF-3, RF-9, RF-16).
    /// </summary>
    bool TryShowExpanded();

    /// <summary>
    /// Muestra el contenido compacto de esta funcionalidad. Devuelve false si
    /// no es usable ahora mismo.
    /// </summary>
    bool TryShowCompact();

    /// <summary>Resumen de una línea para las pantallas combinadas.</summary>
    IslandFeatureSummary Summary { get; }
}

/// <summary>
/// Resumen de una línea de una funcionalidad, para las PANTALLAS COMBINADAS
/// (change island-pantallas RF-4): un icono y un texto corto («78 %», «12:30»,
/// «en 5 min») con el que varias funcionalidades conviven en el compacto. La
/// pantalla de una sola funcionalidad no lo usa: ahí manda su vista rica.
/// </summary>
public readonly record struct IslandFeatureSummary(Wpf.Ui.Controls.SymbolRegular Glyph, string Text);

/// <summary>Snapshot del contrato de una funcionalidad en un instante dado.</summary>
public readonly record struct IslandFeatureState(
    bool Enabled,
    bool Available,
    bool Active,
    bool Selected,
    bool Exclusive)
{
    /// <summary>Puede abrirse por clic o navegación (fuera de presentación y navegación si no).</summary>
    public bool Usable => Enabled && Available;
}

/// <summary>
/// Registro de funcionalidades del contenedor: cada funcionalidad se registra una vez
/// y el contenedor la busca por su id estable (001 MOD RF-3, RF-11). El orden del
/// registro no significa nada —el orden de la vista lo fijan las PANTALLAS—: añadir
/// una funcionalidad futura = registrar otra implementación del contrato, sin tocar
/// ninguna regla del contenedor.
/// </summary>
public sealed class IslandFeatureRegistry
{
    private readonly List<IIslandFeature> _features = [];

    public IReadOnlyList<IIslandFeature> Features => _features;

    public void Register(IIslandFeature feature)
    {
        if (!_features.Any(f => f.Id == feature.Id)) _features.Add(feature);
    }
}

/// <summary>
/// Funcionalidad «media» del island (001): disponible con snapshot musical;
/// activa reproduciendo (y pausada solo si el ajuste «pausa cuenta como
/// activo» lo dice: 001 MOD RF-6).
/// </summary>
public sealed class IslandMediaFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => "media";

    public IslandFeatureState State => owner.GetMediaFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido

    public bool TryShowExpanded() => owner.ShowMediaExpandedFromContract();
    public bool TryShowCompact() => owner.ShowMediaCompactFromContract();

    public IslandFeatureSummary Summary => owner.MediaSummary();
}

/// <summary>
/// Funcionalidad «cajón de aplicaciones» del island: disponible cuando está
/// habilitada y tiene al menos una aplicación configurada (sin aplicaciones no
/// se abre una vista vacía: 001 MOD RF-9). No genera actividad propia —no
/// reproduce ni cuenta—, así que nunca sostiene el compacto por sí sola: su
/// vista la sostienen el puntero (Visible mientras activo) o el aviso temporal.
/// </summary>
public sealed class IslandAppsFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => "apps";

    public IslandFeatureState State => owner.GetAppsFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido (cuadrícula de iconos)

    public bool TryShowExpanded() => owner.ShowAppsExpandedFromContract();
    public bool TryShowCompact() => owner.ShowAppsCompactFromContract();

    public IslandFeatureSummary Summary => owner.AppsSummary();
}

/// <summary>
/// Funcionalidad «estante de archivos» del island: aparca archivos y carpetas que el
/// usuario suelta encima del Island y deja arrastrarlos fuera. Como el cajón, no genera
/// actividad propia —no reproduce ni cuenta—, así que nunca sostiene el compacto por sí
/// sola: su vista la sostienen el puntero (Visible mientras activo) o el aviso temporal
/// (001 MOD RF-4/RF-9, 002 RF-8).
/// </summary>
public sealed class IslandShelfFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Shelf;

    public IslandFeatureState State => owner.GetShelfFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido (mosaicos)

    public bool TryShowExpanded() => owner.ShowShelfExpandedFromContract();
    public bool TryShowCompact() => owner.ShowShelfCompactFromContract();

    public IslandFeatureSummary Summary => owner.ShelfSummary();
}

/// <summary>
/// Funcionalidad «recordatorios de calendario» del island: disponible cuando está
/// habilitada y hay sesión de Google iniciada desde ajustes (sin sesión no hay nada
/// que enseñar). A diferencia del cajón y el estante, SÍ tiene actividad propia: el
/// recordatorio de un evento la mantiene activa mientras el evento está a punto de
/// empezar, así que su vista se sostiene sola y el contenedor se despliega al avisar
/// (001 MOD RF-4).
/// </summary>
public sealed class IslandCalendarFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Calendar;

    public IslandFeatureState State => owner.GetCalendarFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido (lista de eventos)

    public bool TryShowExpanded() => owner.ShowCalendarExpandedFromContract();
    public bool TryShowCompact() => owner.ShowCalendarCompactFromContract();

    public IslandFeatureSummary Summary => owner.CalendarSummary();
}

/// <summary>
/// Funcionalidad «dispositivos Bluetooth» del island: disponible cuando está
/// habilitada y ya se conoce un dispositivo (el último conectado); su vista
/// compacta es SIEMPRE un aviso temporal —nombre y batería durante el plazo
/// configurado—, así que no tiene expandido propio y no genera actividad que
/// sostenga la caja más allá de su aviso (change island-bluetooth-conectado,
/// RF-1/RF-3).
/// </summary>
public sealed class IslandBluetoothFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Bluetooth;

    public IslandFeatureState State => owner.GetBluetoothFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // sin expandido propio
    public double ExpandedPreferredHeight => 0;

    /// <summary>
    /// Sin tarjeta expandida: el aviso de Bluetooth es una notificación (icono,
    /// nombre y batería) y no tiene controles que abrir. Devolver false hace que
    /// el contenedor pruebe la siguiente funcionalidad usable en vez de abrir una
    /// vista vacía (001 MOD RF-3/RF-9).
    /// </summary>
    public bool TryShowExpanded() => false;

    public bool TryShowCompact() => owner.ShowBluetoothCompactFromContract();

    public IslandFeatureSummary Summary => owner.BluetoothSummary();
}

/// <summary>
/// Funcionalidad «portapapeles» del island (change island-portapapeles): disponible
/// cuando está habilitada y hay algo copiado (sin piezas no hay nada que pegar);
/// como el cajón y el estante no genera actividad propia —no reproduce ni cuenta—,
/// así que su vista la sostienen el puntero (Visible mientras activo) o su aviso
/// temporal.
/// </summary>
public sealed class IslandClipboardFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Clipboard;

    public IslandFeatureState State => owner.GetClipboardFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido (lista de piezas)

    public bool TryShowExpanded() => owner.ShowClipboardExpandedFromContract();
    public bool TryShowCompact() => owner.ShowClipboardCompactFromContract();

    public IslandFeatureSummary Summary => owner.ClipboardSummary();
}

/// <summary>
/// Funcionalidad «clima» (change island-clima): disponible cuando está habilitada,
/// hay un lugar elegido y ya llegó un dato (sin dato no hay nada que enseñar); como
/// el cajón y el estante no genera actividad propia —el clima no «está pasando»—,
/// así que su vista la sostienen el puntero (Visible mientras activo) o su plazo
/// (Aviso temporal).
/// </summary>
public sealed class IslandWeatherFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Weather;

    public IslandFeatureState State => owner.GetWeatherFeatureState();

    public double CompactWidth => 200;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0; // ancho común configurado
    public double ExpandedPreferredHeight => 0; // medir contenido

    public bool TryShowExpanded() => owner.ShowWeatherExpandedFromContract();
    public bool TryShowCompact() => owner.ShowWeatherCompactFromContract();

    public IslandFeatureSummary Summary => owner.WeatherSummary();
}

/// <summary>
/// Funcionalidad «cargador» (change island-cargador): avisa cuando el equipo se
/// enchufa y cuando se queda a batería, con el rayo y el nivel. Su vista es un aviso
/// temporal —no tiene expandido— y no genera actividad propia: la sostiene su plazo.
/// </summary>
public sealed class IslandPowerFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => IslandFeatureIds.Power;

    public IslandFeatureState State => owner.GetPowerFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0;
    public double ExpandedPreferredHeight => 0;

    /// <summary>Sin expandido: el aviso del cargador vive en el compacto.</summary>
    public bool TryShowExpanded() => false;

    public bool TryShowCompact() => owner.ShowPowerCompactFromContract();

    public IslandFeatureSummary Summary => owner.PowerSummary();
}

/// <summary>
/// Funcionalidad «temporizador» del island (002): disponible siempre que esté
/// habilitada (su configuración es usable sin media); activa con cuenta en
/// marcha; la alerta final declara acceso exclusivo persistente (002 MOD RF-2).
/// </summary>
public sealed class IslandTimerFeature(IslandWindow owner) : IIslandFeature
{
    public string Id => "timer";

    public IslandFeatureState State => owner.GetTimerFeatureState();

    public double CompactWidth => 240;
    public double CompactHeight => 34;
    public double ExpandedPreferredWidth => 0;
    public double ExpandedPreferredHeight => 0; // medir contenido

    public bool TryShowExpanded() => owner.ShowTimerExpandedFromContract();
    public bool TryShowCompact() => owner.ShowTimerCompactFromContract();

    public IslandFeatureSummary Summary => owner.TimerSummary();
}
