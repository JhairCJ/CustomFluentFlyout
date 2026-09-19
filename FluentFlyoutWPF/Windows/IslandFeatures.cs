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

    /// <summary>
    /// Reordena las funcionalidades según los identificadores dados: es el orden en el
    /// que el contenedor navega (rueda y flechas) y con el que desempata cuando varias
    /// están activas a la vez (001 MOD RF-3/RF-4). Los ids no listados quedan al final,
    /// en su orden actual, así que un ajuste viejo nunca deja una funcionalidad fuera.
    /// </summary>
    public void Reorder(IReadOnlyList<string>? ids)
    {
        if (ids == null || ids.Count == 0) return;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++) index.TryAdd(ids[i], i);
        var ordered = _features.Select((feature, position) => (feature, position)).ToList();
        ordered.Sort((a, b) =>
        {
            int ia = index.TryGetValue(a.feature.Id, out int va) ? va : int.MaxValue;
            int ib = index.TryGetValue(b.feature.Id, out int vb) ? vb : int.MaxValue;
            return ia != ib ? ia.CompareTo(ib) : a.position.CompareTo(b.position);
        });
        _features.Clear();
        _features.AddRange(ordered.Select(x => x.feature));
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
