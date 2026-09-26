// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;
using System.Globalization;
using System.Windows;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Punto único de los textos de UI del Island en la capa WPF.
///
/// <para>Resuelve una clave del diccionario de localización con un respaldo en inglés,
/// de modo que un idioma sin traducir —o un diccionario incompleto— nunca deja el texto
/// en blanco: el diccionario <c>en-US</c> va SIEMPRE cargado como base
/// (<c>LocalizationManager</c>) y solo el idioma elegido se apila encima.</para>
///
/// <para>En XAML no hace falta pasar por aquí: se usa <c>{DynamicResource Clave}</c>,
/// que además se refresca solo al cambiar de idioma. Este ayudante existe para los
/// textos que se escriben desde código (tooltips, estados, mensajes de error).</para>
/// </summary>
public static class IslandStrings
{
    /// <summary>
    /// Texto localizado de <paramref name="key"/>, o <paramref name="fallback"/> (inglés)
    /// si no existe. Un fallo de resolución devuelve el respaldo en vez de propagar la
    /// excepción: el vigía de Bluetooth y el bucle del calendario escriben desde hilos
    /// de fondo, donde tocar recursos de WPF puede lanzar por afinidad de hilo.
    /// </summary>
    public static string Get(string key, string fallback)
    {
        try
        {
            return Application.Current?.TryFindResource(key)?.ToString() is { Length: > 0 } text
                ? text
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Texto localizado con formato (usa la cultura actual).</summary>
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, fallback), args);

    /// <summary>Nombre visible de una funcionalidad (clave declarada en <see cref="IslandFeatureIds.DisplayNameKey"/>).</summary>
    public static string FeatureName(string? id) =>
        Get(IslandFeatureIds.DisplayNameKey(id), id ?? "");
}
