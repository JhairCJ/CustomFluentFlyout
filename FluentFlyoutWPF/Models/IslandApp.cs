// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Utils;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Aplicación del cajón del Island: nombre visible y ruta de lanzamiento.
/// La validación rechaza en español y conserva el valor anterior (mismo criterio
/// que los presets del temporizador). El icono se extrae del ejecutable la
/// primera vez que se pinta y NO persiste: el XML solo guarda nombre y ruta.
/// </summary>
public sealed class IslandApp : INotifyPropertyChanged
{
    /// <summary>Máximo de aplicaciones del cajón.</summary>
    public const int MaxApps = 12;

    /// <summary>Cuántas aplicaciones caben en la fila compacta (el resto, en el expandido).</summary>
    public const int MaxCompactApps = 7;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name = "";
    private string _path = "";
    private ImageSource? _icon;
    private bool _iconLoaded;

    /// <summary>
    /// Nombre visible. Vacío se rechaza conservando el anterior.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Error = "El nombre no puede estar vacío.";
                OnPropertyChanged(nameof(Error));
                OnPropertyChanged(nameof(Name));
                return;
            }
            if (_name == value)
            {
                if (Error != "")
                {
                    Error = "";
                    OnPropertyChanged(nameof(Error));
                }
                return;
            }
            _name = value;
            Error = "";
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(Error));
        }
    }

    /// <summary>
    /// Ruta del ejecutable (o de su acceso directo). La fija el alta desde
    /// ajustes; los ajustes no la editan.
    /// </summary>
    public string Path
    {
        get => _path;
        set
        {
            string fixedValue = (value ?? "").Trim();
            if (_path == fixedValue) return;
            _path = fixedValue;
            _icon = null;
            _iconLoaded = false;
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(Icon));
        }
    }

    /// <summary>
    /// Nombre y ruta en una línea: lo que muestra el tooltip de cada icono.
    /// </summary>
    [XmlIgnore]
    public string ToolTip => string.IsNullOrWhiteSpace(_path) ? _name : $"{_name}\n{_path}";

    /// <summary>
    /// ¿La ruta sigue existiendo? Un ejecutable borrado no se puede lanzar.
    /// </summary>
    [XmlIgnore]
    public bool Exists => _path.Length > 0 && File.Exists(_path);

    /// <summary>
    /// Icono del ejecutable, extraído la primera vez que se pinta (misma
    /// extracción que el flyout de medios). null = sin icono (el mosaico queda
    /// con su fondo). No persiste.
    /// </summary>
    [XmlIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            try { _icon = Exists ? MediaPlayerData.GetExecutableIcon(_path) : null; }
            catch { _icon = null; }
            return _icon;
        }
    }

    /// <summary>
    /// Último mensaje de validación en español. Vacío = sin error. No persiste.
    /// </summary>
    [XmlIgnore]
    public string Error { get; private set; } = "";

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
