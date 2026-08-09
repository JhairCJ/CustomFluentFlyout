// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;

namespace FluentFlyoutWPF.Classes.Converters;

public class PercentageToOpacityConverter : IValueConverter
{
    // Convert percentage (0-100) to opacity (0.0-1.0)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return Math.Clamp(intValue, 0, 100) / 100.0;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double doubleValue)
        {
            return (int)Math.Round(Math.Clamp(doubleValue, 0.0, 1.0) * 100);
        }
        return 0;
    }
}
