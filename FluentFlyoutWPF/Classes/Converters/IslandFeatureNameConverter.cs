// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;
using System.Globalization;
using System.Windows.Data;

namespace FluentFlyoutWPF.Classes.Converters;

/// <summary>
/// Id de funcionalidad del Island («media», «timer»…) → nombre visible en ajustes
/// («Control multimedia», «Temporizador»…). La lista de orden se guarda con ids —son
/// estables y no dependen del idioma— y en pantalla se muestra el nombre.
/// </summary>
public class IslandFeatureNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        IslandFeatureIds.DisplayName(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
