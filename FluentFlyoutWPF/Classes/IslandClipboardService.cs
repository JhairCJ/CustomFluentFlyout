// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentFlyoutWPF.Classes;

/// <summary>Qué lleva un elemento del portapapeles.</summary>
public enum IslandClipboardKind
{
    Text,
    Image,
}

/// <summary>
/// Elemento del portapapeles (change island-portapapeles RF-1/RF-2): texto o
/// imagen, con su miniatura para pintarlo sin reescalar el original en cada
/// repintado. El original se conserva intacto para devolverlo al portapapeles al
/// hacer clic (copiar de nuevo NO puede degradar lo que el usuario copió).
/// </summary>
public sealed class IslandClipboardItem
{
    /// <summary>
    /// Cuántas piezas caben en el renglón compacto. La celda es la misma que la del
    /// cajón y el estante (20 + 4 de paso), así que caben las mismas: 7 en cápsula y
    /// 6 en el notch, que es más estrecho por diseño.
    /// </summary>
    public const int MaxCompactItems = 7;
    public const int MaxCompactItemsNotch = 6;

    public required IslandClipboardKind Kind { get; init; }

    /// <summary>Texto copiado (vacío en una imagen).</summary>
    public string Text { get; init; } = "";

    /// <summary>Imagen original copiada (null en un texto).</summary>
    public BitmapSource? Image { get; init; }

    /// <summary>Miniatura congelada para las vistas del Island.</summary>
    public BitmapSource? Preview { get; init; }

    public DateTime When { get; init; } = DateTime.Now;

    /// <summary>¿Es la misma pieza que esta otra? (para no repetir copias seguidas)</summary>
    public bool SameAs(IslandClipboardItem other) =>
        Kind == other.Kind
        && (Kind == IslandClipboardKind.Text
            ? string.Equals(Text, other.Text, StringComparison.Ordinal)
            : ReferenceEquals(Image, other.Image) || (Image != null && other.Image != null
                && Image.PixelWidth == other.Image.PixelWidth
                && Image.PixelHeight == other.Image.PixelHeight));

    /// <summary>Texto del elemento para el tooltip y para las pantallas combinadas.</summary>
    public string Caption => Kind == IslandClipboardKind.Image ? "Imagen" : Shorten(Text, 120);

    /// <summary>Primeras letras del texto, para la ficha minúscula del compacto.</summary>
    public string Initials => Kind == IslandClipboardKind.Image
        ? ""
        : new string(Shorten(Text, 3).Where(ch => !char.IsWhiteSpace(ch)).Take(2).ToArray());

    private static string Shorten(string text, int max)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

/// <summary>
/// Portapapeles del Island por EVENTOS (change island-portapapeles): una ventana
/// de mensajes se registra con <c>AddClipboardFormatListener</c> y recibe
/// <c>WM_CLIPBOARDUPDATE</c> cada vez que algo se copia. Nada de sondear el
/// portapapeles: sin copia no hay trabajo.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Solo texto e imagen</b> (RF-1): lo que copie cualquier aplicación y sea
/// texto o imagen entra en la lista, lo más reciente primero.</item>
/// <item><b>Copiar de vuelta sin duplicar</b> (RF-3): devolver un elemento al
/// portapapeles no vuelve a añadirlo (la propia escritura se ignora una vez), así
/// la lista no se llena de copias de sí misma.</item>
/// <item><b>Lista acotada</b> (RF-2): <see cref="MaxItems"/> elementos; el más
/// viejo cae cuando entra uno nuevo.</item>
/// <item><b>Original intacto</b> (RF-4): se guarda la imagen original y una
/// miniatura aparte, así copiar de nuevo devuelve exactamente lo copiado.</item>
/// </list>
///
/// <para>Todo ocurre en el hilo de UI (el portapapeles de WPF es STA): la ventana
/// de mensajes se crea en él y sus callbacks corren en él.</para>
/// </summary>
public sealed class IslandClipboardService : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    /// <summary>Tope de texto guardado por elemento (una copia gigante no hincha la lista).</summary>
    private const int MaxTextLength = 20000;
    /// <summary>Lado máximo de la miniatura de una imagen.</summary>
    private const int PreviewSide = 160;

    private readonly List<IslandClipboardItem> _items = [];
    private HwndSource? _source;
    private bool _ignoreNext;
    private bool _disposed;

    /// <summary>Elementos vivos, del más reciente al más viejo.</summary>
    public IReadOnlyList<IslandClipboardItem> Items => _items;

    /// <summary>Cuántos elementos se conservan (el resto cae por el final).</summary>
    public int MaxItems { get; set; } = 20;

    /// <summary>La lista cambió (alta, baja o vaciado): el Island repinta.</summary>
    public event Action? Changed;

    /// <summary>¿Está escuchando el portapapeles? Con él parado no se captura nada.</summary>
    public bool IsListening => _source != null;

    /// <summary>
    /// Arranca la escucha con una ventana de mensajes propia (HWND_MESSAGE): no toca
    /// la ventana del Island ni su ciclo de vida. Idempotente.
    /// </summary>
    public void Start()
    {
        if (_disposed || _source != null) return;
        try
        {
            var parameters = new HwndSourceParameters("FluentFlyoutClipboardListener")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            };
            var source = new HwndSource(parameters);
            source.AddHook(WndProc);
            if (!NativeMethods.AddClipboardFormatListener(source.Handle))
            {
                source.RemoveHook(WndProc);
                source.Dispose();
                Log.Warn("Portapapeles: el sistema no aceptó el registro de notificaciones");
                return;
            }
            _source = source;
        }
        catch (Exception ex)
        {
            _source = null;
            Log.Warn(ex, "Portapapeles: no se pudo escuchar el portapapeles");
        }
    }

    /// <summary>Deja de escuchar y suelta la ventana de mensajes.</summary>
    public void Stop()
    {
        var source = _source;
        _source = null;
        if (source == null) return;
        try
        {
            NativeMethods.RemoveClipboardFormatListener(source.Handle);
            source.RemoveHook(WndProc);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Portapapeles: fallo al soltar la escucha");
        }
        source.Dispose();
    }

    /// <summary>Vacía la lista (ajuste de ajustes y del propio Island).</summary>
    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Devuelve un elemento al portapapeles (el clic en la lista del Island). La
    /// escritura se marca para que su propia notificación no lo vuelva a añadir
    /// (RF-3); el elemento sube al principio (es lo último usado).
    /// </summary>
    public void Copy(IslandClipboardItem item)
    {
        try
        {
            if (item.Kind == IslandClipboardKind.Image && item.Image != null)
            {
                _ignoreNext = true;
                Clipboard.SetImage(item.Image);
            }
            else if (item.Text.Length > 0)
            {
                _ignoreNext = true;
                Clipboard.SetText(item.Text);
            }
            else
            {
                return;
            }
            if (_items.Remove(item)) _items.Insert(0, item);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // El portapapeles puede estar bloqueado por otra aplicación justo ahora.
            _ignoreNext = false;
            Log.Warn(ex, "Portapapeles: no se pudo devolver el elemento al portapapeles");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // --- captura ---

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        // La copia la hizo el propio Island: no vuelve a entrar en la lista.
        if (_ignoreNext)
        {
            _ignoreNext = false;
            return IntPtr.Zero;
        }
        Capture();
        return IntPtr.Zero;
    }

    /// <summary>
    /// Lee el portapapeles y añade lo que haya. Si otra aplicación lo tiene
    /// bloqueado en ese instante (COMException) se descarta ESA captura: la
    /// siguiente copia vuelve a avisar.
    /// </summary>
    private void Capture()
    {
        try
        {
            IslandClipboardItem? item = null;
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    item = new IslandClipboardItem
                    {
                        Kind = IslandClipboardKind.Image,
                        Image = image,
                        Preview = MakePreview(image),
                    };
                }
            }
            else if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    item = new IslandClipboardItem
                    {
                        Kind = IslandClipboardKind.Text,
                        Text = text.Length > MaxTextLength ? text[..MaxTextLength] : text,
                    };
                }
            }
            if (item == null) return;
            Add(item);
        }
        catch (COMException)
        {
            // Portapapeles ocupado por otra aplicación: se ignora esta captura.
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Portapapeles: no se pudo leer lo copiado");
        }
    }

    private void Add(IslandClipboardItem item)
    {
        if (_items.Count > 0 && _items[0].SameAs(item)) return; // copia repetida
        _items.Insert(0, item);
        int max = Math.Clamp(MaxItems, 1, 100);
        while (_items.Count > max) _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Miniatura congelada de una imagen (el original no se toca: RF-4).</summary>
    private static BitmapSource MakePreview(BitmapSource source)
    {
        try
        {
            int maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (maxSide <= PreviewSide)
            {
                if (source.CanFreeze) source.Freeze();
                return source;
            }
            double scale = PreviewSide / (double)maxSide;
            var preview = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            preview.Freeze();
            return preview;
        }
        catch
        {
            return source;
        }
    }
}
