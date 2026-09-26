// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Xml.Serialization;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Archivo o carpeta aparcado en el estante del Island.
///
/// <para>El estante es un aparcamiento temporal: al soltar algo encima se MUEVE a la
/// carpeta propia del estante (<c>UserSettings.IslandShelfFolder</c>) y se anota de
/// dónde vino (<see cref="Origin"/>). Así arrastrarlo fuera lo saca y quitarlo del
/// estante lo devuelve a su sitio: desde aquí NUNCA se borra nada del usuario. Los
/// elementos no caducan, solo salen cuando el usuario los saca o los quita.</para>
///
/// <para>Como el cajón de aplicaciones: solo se persisten ruta, origen y tipo; lo
/// visible (nombre, etiqueta, glifo) se deduce de la ruta al pintar.</para>
/// </summary>
public sealed class IslandShelfItem
{
    /// <summary>Máximo de elementos del estante.</summary>
    public const int MaxItems = 24;

    /// <summary>Cuántos caben en la fila compacta del estilo pill (el resto, contados).</summary>
    public const int MaxCompactItems = 7;

    /// <summary>
    /// Cuántos caben en el compacto del estilo notch, más estrecho por diseño (001
    /// RF-15). La celda del renglón es la misma que la del cajón (20 de caja + 4 de
    /// separación = 24 de paso), así que encajan los mismos que allí.
    /// </summary>
    public const int MaxCompactItemsNotch = 6;

    /// <summary>Ruta del elemento dentro de la carpeta del estante.</summary>
    public string Path { get; set; } = "";

    /// <summary>Carpeta o archivo del que vino, para poder devolverlo al quitarlo.</summary>
    public string Origin { get; set; } = "";

    /// <summary>¿Es una carpeta? El estante guarda las dos cosas igual.</summary>
    public bool IsFolder { get; set; }

    /// <summary>Nombre visible: el del archivo o carpeta. No persiste.</summary>
    [XmlIgnore]
    public string Name
    {
        get
        {
            string name = System.IO.Path.GetFileName(Path);
            if (name.Length > 0) return name;
            name = System.IO.Path.GetFileName(Origin);
            return name.Length > 0 ? name : Path;
        }
    }

    /// <summary>
    /// Extensión sin punto y en mayúsculas ("" si no tiene). No persiste. El estante
    /// enseña el NOMBRE del elemento, nunca su tipo: el glifo ya dice si es carpeta o
    /// documento, y «CARPETA» no le dice al usuario cuál de sus carpetas es.
    /// </summary>
    [XmlIgnore]
    public string Extension
    {
        get
        {
            try { return System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(); }
            catch { return ""; }
        }
    }

    /// <summary>Glifo del renglón compacto (documento o carpeta). No persiste.</summary>
    [XmlIgnore]
    public SymbolRegular Glyph => IsFolder ? SymbolRegular.Folder20 : SymbolRegular.Document20;

    /// <summary>¿Sigue existiendo el elemento en el estante? No persiste.</summary>
    [XmlIgnore]
    public bool Exists => Path.Length > 0 && (File.Exists(Path) || Directory.Exists(Path));

    /// <summary>Nombre y ruta en una línea: el tooltip de cada mosaico. No persiste.</summary>
    [XmlIgnore]
    public string ToolTip => Path.Length == 0 ? Name : $"{Name}\n{Path}";

    /// <summary>Ruta del elemento sin decorar: la que ve el usuario en ajustes.</summary>
    [XmlIgnore]
    public string DisplayPath => Origin.Length > 0 ? Origin : Path;
}
