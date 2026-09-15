// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;

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
}

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
/// Registro de funcionalidades del contenedor: una lista ordenada que el
/// island recorre para elegir la última usable (001 MOD RF-3, RF-11).
/// Añadir una funcionalidad futura = registrar otra implementación del
/// contrato; ninguna regla del contenedor cambia.
/// </summary>
public sealed class IslandFeatureRegistry
{
    private readonly List<IIslandFeature> _features = [];

    public IReadOnlyList<IIslandFeature> Features => _features;

    public void Register(IIslandFeature feature)
    {
        if (!_features.Any(f => f.Id == feature.Id)) _features.Add(feature);
    }

    /// <summary>Funcionalidades habilitadas y disponibles (usables) en orden de registro.</summary>
    public IEnumerable<IIslandFeature> UsableFeatures() =>
        _features.Where(f => f.State.Usable);

    /// <summary>Funcionalidades activas (con actividad real) en orden de registro.</summary>
    public IEnumerable<IIslandFeature> ActiveFeatures() =>
        _features.Where(f => f.State.Enabled && f.State.Active);

    /// <summary>Índice de la funcionalidad seleccionada en la lista de registradas; -1 si ninguna.</summary>
    public int SelectedIndex()
    {
        for (int i = 0; i < _features.Count; i++)
            if (_features[i].State.Selected) return i;
        return -1;
    }

    public IIslandFeature? SelectedFeature()
    {
        int i = SelectedIndex();
        return i >= 0 ? _features[i] : null;
    }

    /// <summary>Última usable distinta de <paramref name="exclude"/>, si la hay.</summary>
    public IIslandFeature? NextUsableAfter(IIslandFeature? exclude)
    {
        var usable = UsableFeatures().Where(f => !ReferenceEquals(f, exclude)).ToList();
        return usable.Count > 0 ? usable[0] : null;
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
}
