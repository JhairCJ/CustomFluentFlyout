// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Clase de una entrada del registro de actividad (change island-lista-de-activos).
/// </summary>
public enum IslandActivityKind
{
    /// <summary>Actividad VIVA: reproduce, cuenta, recuerda algo. Vive hasta que su dueño la cierra.</summary>
    Live,

    /// <summary>AVISO con plazo: vive hasta su vencimiento (dispositivos, cargador, una copia…).</summary>
    Notice,

    /// <summary>ACCESO EXCLUSIVO: manda sobre todo y atraviesa la supresión (alerta, dictado).</summary>
    Exclusive,
}

/// <summary>
/// Una entrada del registro: qué funcionalidad está activa, desde cuándo y hasta cuándo.
/// El instante de nacimiento es lo que desempata «la más reciente» (001 MOD RF-4/RF-12).
/// </summary>
public readonly record struct IslandActivityEntry(
    string Id,
    IslandActivityKind Kind,
    DateTime StartedUtc,
    DateTime ExpiresUtc,
    bool AlwaysTemporal = false)
{
    /// <summary>¿Sigue viva en el instante dado? Una actividad viva nunca vence sola.</summary>
    public bool Alive(DateTime now) => ExpiresUtc > now;
}

/// <summary>
/// LISTA DE EVENTOS ACTIVOS del Island (change island-lista-de-activos): el contenedor
/// mantiene UNA lista con lo que está pasando —música reproduciendo, temporizador
/// contando, un aviso con su plazo, una exclusiva— y de ella sale la vista que se
/// muestra. Antes ese mismo dato estaba repartido entre el snapshot musical, la hora
/// del aviso (<c>_noticeUntil</c>), las banderas de «sigue vivo» de cada funcionalidad y
/// las decisiones dispersas de la máquina de estados, y bastaba que uno de esos sitios
/// no coincidiera para que algo activo no se viera.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Una entrada por funcionalidad y clase:</b> la ACTIVIDAD viva (reproducir,
/// contar, un recordatorio) la declara su dueño y la cierra él mismo; un AVISO nace con
/// su plazo y lo renueva su evento, nunca una repetición del mismo (001 RF-2).</item>
/// <item><b>Sin reloj propio:</b> cada consulta recibe el instante, así que la clase es
/// PURA y se comprueba sin dormir en <c>.selfcheck</c>. No hay hilos ni temporizadores:
/// el vencimiento lo cumple el despertador único que ya existe.</item>
/// <item><b>El instante de nacimiento es el desempate:</b> con varias activas gana la del
/// evento más reciente y, a igualdad, la primera del orden de las pantallas
/// (<see cref="IslandActivityPick"/>).</item>
/// <item><b>La exclusiva manda:</b> mientras hay una entrada exclusiva viva, ninguna otra
/// actividad decide la vista (001 RF-8/14, 002 RF-2).</item>
/// </list>
///
/// <para>Parte del IslandWindow; la traducción de esta lista a una vista está en
/// <c>IslandPresentation.cs</c>.</para>
/// </summary>
public sealed class IslandActivityRegistry
{
    /// <summary>Actividad viva y exclusivas, por funcionalidad.</summary>
    private readonly Dictionary<string, IslandActivityEntry> _entries = new(StringComparer.Ordinal);
    /// <summary>Avisos con plazo, por funcionalidad (ortogonales a la actividad viva).</summary>
    private readonly Dictionary<string, IslandActivityEntry> _notices = new(StringComparer.Ordinal);

    /// <summary>¿Hay algo registrado (vivo o vencido) para esta funcionalidad?</summary>
    public bool Contains(string id) => _entries.ContainsKey(id) || _notices.ContainsKey(id);

    /// <summary>
    /// Declara (o retira) la actividad VIVA de una funcionalidad. Se llama en cada
    /// reconciliación: repetir el mismo estado no toca la entrada, así su instante de
    /// nacimiento —el desempate— sobrevive a los refrescos.
    /// </summary>
    public void SetLive(string id, bool active, DateTime now)
    {
        if (active)
        {
            if (!_entries.ContainsKey(id))
                _entries[id] = new IslandActivityEntry(id, IslandActivityKind.Live, now, DateTime.MaxValue);
            return;
        }
        if (_entries.TryGetValue(id, out var entry) && entry.Kind == IslandActivityKind.Live)
            _entries.Remove(id);
    }

    /// <summary>
    /// Nace un AVISO con su plazo (evento nuevo): fija el instante de nacimiento, que es
    /// lo que lo hace ganar el desempate frente a lo que ya estaba activo. Con
    /// <paramref name="alwaysTemporal"/> el aviso vence incluso en «Visible mientras
    /// activo» (Bluetooth, cargador: su vista es una notificación, no un estado).
    /// </summary>
    public void Pulse(string id, DateTime now, DateTime until, bool alwaysTemporal = false) =>
        _notices[id] = new IslandActivityEntry(id, IslandActivityKind.Notice, now, until, alwaysTemporal);

    /// <summary>
    /// Renueva el plazo de un aviso SIN tocar su instante de nacimiento: es lo que hace un
    /// reintento de vencimiento (la vista no puede replegarse bajo el cursor). Devuelve
    /// false si no había aviso que renovar.
    /// </summary>
    public bool Renew(string id, DateTime until)
    {
        if (!_notices.TryGetValue(id, out var notice)) return false;
        _notices[id] = notice with { ExpiresUtc = until };
        return true;
    }

    /// <summary>Declara (o retira) el acceso EXCLUSIVO de una funcionalidad.</summary>
    public void SetExclusive(string id, bool on, DateTime now)
    {
        if (on)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Kind != IslandActivityKind.Exclusive)
                _entries[id] = new IslandActivityEntry(id, IslandActivityKind.Exclusive, now, DateTime.MaxValue);
            return;
        }
        if (_entries.TryGetValue(id, out var current) && current.Kind == IslandActivityKind.Exclusive)
            _entries.Remove(id);
    }

    /// <summary>Cierra la actividad viva (o la exclusiva) de una funcionalidad; su aviso se queda.</summary>
    public void End(string id) => _entries.Remove(id);

    /// <summary>Olvida todo lo de una funcionalidad: actividad y aviso.</summary>
    public void Forget(string id)
    {
        _entries.Remove(id);
        _notices.Remove(id);
    }

    /// <summary>
    /// Olvida SOLO el aviso de una funcionalidad: su actividad viva (si la tiene) se queda
    /// sosteniendo la vista. Una pausa que cuenta como activo es un estado, no un aviso: al
    /// vencer el plazo del aviso, la pausa sigue siendo actividad (change island-lista-de-activos).
    /// </summary>
    public void ForgetNotice(string id) => _notices.Remove(id);

    /// <summary>¿Tiene esta funcionalidad algo vivo ahora mismo (actividad o aviso)?</summary>
    public bool IsAlive(string id, DateTime now) => Live(id, now) is not null;

    /// <summary>
    /// ¿Tiene esta funcionalidad una actividad VIVA (no un aviso) ahora mismo? Es lo que
    /// distingue un ESTADO que sostiene la vista de una notificación con plazo.
    /// </summary>
    public bool HasLive(string id, DateTime now) =>
        _entries.TryGetValue(id, out var entry) && entry.Kind == IslandActivityKind.Live && entry.Alive(now);

    /// <summary>¿Tiene esta funcionalidad un AVISO vivo ahora mismo?</summary>
    public bool NoticeAlive(string id, DateTime now) =>
        _notices.TryGetValue(id, out var notice) && notice.Alive(now);

    /// <summary>¿El aviso vivo de esta funcionalidad vence también en «Visible mientras activo»?</summary>
    public bool NoticeAlwaysTemporal(string id, DateTime now) =>
        _notices.TryGetValue(id, out var notice) && notice.Alive(now) && notice.AlwaysTemporal;

    /// <summary>Instante de nacimiento del aviso vivo de una funcionalidad (MinValue si no hay).</summary>
    public DateTime NoticeStarted(string id, DateTime now) =>
        _notices.TryGetValue(id, out var notice) && notice.Alive(now) ? notice.StartedUtc : DateTime.MinValue;

    /// <summary>Vencimiento del aviso vivo de una funcionalidad (MinValue si no hay): es el plazo presentado.</summary>
    public DateTime NoticeExpiry(string id, DateTime now) =>
        _notices.TryGetValue(id, out var notice) && notice.Alive(now) ? notice.ExpiresUtc : DateTime.MinValue;

    /// <summary>Funcionalidad con acceso exclusivo vigente (null si no hay ninguna).</summary>
    public string? ExclusiveId(DateTime now)
    {
        foreach (var entry in _entries.Values)
            if (entry.Kind == IslandActivityKind.Exclusive && entry.Alive(now)) return entry.Id;
        return null;
    }

    /// <summary>¿Hay acceso exclusivo vigente?</summary>
    public bool HasExclusive(DateTime now) => ExclusiveId(now) != null;

    /// <summary>
    /// Entrada viva de una funcionalidad: la más reciente de sus dos caras (actividad viva
    /// o aviso) o null si no tiene ninguna. Es la consulta que usa la política de vista.
    /// </summary>
    public IslandActivityEntry? Live(string id, DateTime now)
    {
        IslandActivityEntry? best = null;
        if (_entries.TryGetValue(id, out var live) && live.Alive(now)) best = live;
        if (_notices.TryGetValue(id, out var notice) && notice.Alive(now)
            && (best == null || notice.StartedUtc > best.Value.StartedUtc))
            best = notice;
        return best;
    }

    /// <summary>
    /// TODAS las entradas vivas, una por funcionalidad: es la lista de eventos activos que
    /// decide la vista. Su orden no significa nada (la política ordena por instante y, a
    /// igualdad, por el orden de las pantallas que recibe aparte).
    /// </summary>
    public List<IslandActivityEntry> Alive(DateTime now)
    {
        var list = new List<IslandActivityEntry>(_entries.Count + _notices.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Concat(_notices.Keys))
        {
            if (!seen.Add(id)) continue;
            if (Live(id, now) is { } entry) list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Vencimiento más cercano entre los avisos vivos: es la hora que arma el despertador
    /// único del aviso. MinValue cuando no hay ninguno.
    /// </summary>
    public DateTime NextNoticeExpiry(DateTime now)
    {
        DateTime next = DateTime.MinValue;
        foreach (var notice in _notices.Values)
        {
            if (!notice.Alive(now)) continue;
            if (next == DateTime.MinValue || notice.ExpiresUtc < next) next = notice.ExpiresUtc;
        }
        return next;
    }
}
