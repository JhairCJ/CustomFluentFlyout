// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes;
using System.ComponentModel;
using System.Xml.Serialization;

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Preset de temporizador editable en ajustes: nombre y duración.
/// La validación rechaza en español y conserva el valor anterior (spec 002 RF-12).
/// </summary>
public sealed class TimerPreset : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name = "";
    private int _durationSeconds;

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
                Error = IslandStrings.Get("IslandNameEmpty", "The name cannot be empty.");
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
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(Error));
        }
    }

    /// <summary>
    /// Duración en segundos (1..86400). Solo la fija el parseo validado.
    /// </summary>
    public int DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            int fixedValue = Math.Clamp(value, 1, Classes.IslandTimer.MaxDurationSeconds);
            if (_durationSeconds == fixedValue) return;
            _durationSeconds = fixedValue;
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>
    /// Edición en formato h:mm:ss o m:ss o segundos. Entrada parcial inválida
    /// se rechaza con mensaje y se restaura el texto anterior (plan 002, riesgo campo).
    /// </summary>
    [XmlIgnore]
    public string DurationText
    {
        get => Classes.IslandTimer.FormatHms(TimeSpan.FromSeconds(_durationSeconds));
        set
        {
            if (Classes.IslandTimer.TryParseDuration(value, out var duration))
            {
                Error = "";
                DurationSeconds = (int)duration.TotalSeconds;
            }
            else
            {
                Error = IslandStrings.Get("IslandInvalidDuration", "Invalid duration: use 00:00:01 to 24:00:00.");
            }
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(Error));
        }
    }

    /// <summary>
    /// Fila visible en listas: los duplicados se distinguen por la duración (RF-12).
    /// </summary>
    [XmlIgnore]
    public string Display => $"{_name} · {Classes.IslandTimer.FormatHms(TimeSpan.FromSeconds(_durationSeconds))}";

    /// <summary>
    /// Último mensaje de validación en español. Vacío = sin error. No persiste.
    /// </summary>
    [XmlIgnore]
    public string Error { get; private set; } = "";

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
