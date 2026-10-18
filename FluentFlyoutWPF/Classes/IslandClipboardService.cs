// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Threading;
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
    public string ContentHash { get; init; } = "";
    public long MemoryBytes => (long)Text.Length * sizeof(char) + ImageBytes(Image) + (ReferenceEquals(Image, Preview) ? 0 : ImageBytes(Preview));
    private static long ImageBytes(BitmapSource? image) => image == null ? 0 : (long)image.PixelWidth * image.PixelHeight * Math.Max(4, (image.Format.BitsPerPixel + 7) / 8);

    /// <summary>¿Es la misma pieza que esta otra? (para no repetir copias seguidas)</summary>
    public bool SameAs(IslandClipboardItem other) =>
        Kind == other.Kind
        && (Kind == IslandClipboardKind.Text
            ? string.Equals(Text, other.Text, StringComparison.Ordinal)
            : ReferenceEquals(Image, other.Image) && Image != null || ContentHash.Length > 0 && ContentHash == other.ContentHash);

    /// <summary>Texto del elemento para el tooltip y para las pantallas combinadas.</summary>
    public string Caption => Kind == IslandClipboardKind.Image ? IslandStrings.Get("IslandClipboardImage", "Image") : Shorten(Text, 120);

    /// <summary>Primeras letras del texto, para la ficha minúscula del compacto.</summary>
    public string Initials => Kind == IslandClipboardKind.Image
        ? ""
        : new string(Shorten(Text, 3).Where(ch => !char.IsWhiteSpace(ch)).Take(2).ToArray());

    private static string Shorten(string text, int max)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        int length = Math.Min(trimmed.Length, max);
        if (length > 0 && length < trimmed.Length && char.IsHighSurrogate(trimmed[length - 1])) length--;
        string flat = new string(trimmed[..length]).Replace('\r', ' ').Replace('\n', ' ');
        return length == trimmed.Length ? flat : flat + "…";
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
    internal long MemoryBudgetBytes { get; set; } = 128L * 1024 * 1024;
    private readonly Action<IDataObject> _writeClipboard;
    private readonly Func<uint> _sequenceNumber;
    private readonly Func<int, Task> _retryDelay;

    public IslandClipboardService(Action<IDataObject>? writeClipboard = null, Func<uint>? sequenceNumber = null, Func<int, Task>? retryDelay = null)
    {
        _writeClipboard = writeClipboard ?? (data => Clipboard.SetDataObject(data, true));
        _sequenceNumber = sequenceNumber ?? GetClipboardSequenceNumber;
        _retryDelay = retryDelay ?? (milliseconds => Task.Delay(milliseconds));
    }
    private const string OriginFormat = "FluentFlyout.Island.ClipboardOrigin";
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _generation, _writeVersion;
    private long _memoryBytes;
    public string Error { get; private set; } = "";

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    /// <summary>Lado máximo de la miniatura de una imagen.</summary>
    private const int PreviewSide = 160;

    private readonly List<IslandClipboardItem> _items = [];
    private HwndSource? _source;

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
        ++_generation;
        ++_writeVersion;
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
        ++_writeVersion;
        ++_generation;
        _items.Clear();
        _memoryBytes = 0;
        Error = "";
        Changed?.Invoke();
    }

    /// <summary>
    /// Devuelve un elemento al portapapeles (el clic en la lista del Island). La
    /// escritura se marca para que su propia notificación no lo vuelva a añadir
    /// (RF-3); el elemento sube al principio (es lo último usado).
    /// </summary>
    public async Task<bool> CopyAsync(IslandClipboardItem item)
    {
        if (_disposed) return false;
        int version = ++_writeVersion;
        uint sequence = _sequenceNumber();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_disposed || version != _writeVersion || (attempt > 0 && _sequenceNumber() != sequence)) return false;
            try
            {
                var data = new DataObject();
                if (item.Kind == IslandClipboardKind.Image && item.Image != null) data.SetImage(item.Image);
                else if (item.Text.Length > 0) data.SetText(item.Text);
                else return false;
                data.SetData(OriginFormat, _origin);
                _writeClipboard(data);
                Error = "";
                if (_items.Remove(item)) _items.Insert(0, item);
                Changed?.Invoke();
                return true;
            }
            catch (COMException) when (attempt < 2) { await _retryDelay(attempt == 0 ? 50 : 150); }
            catch (Exception ex)
            {
                Log.Warn(ex, "Island clipboard write failed");
                break;
            }
        }
        if (!_disposed && version == _writeVersion)
        {
            Error = IslandStrings.Get("IslandClipboardBusy", "The clipboard is busy. Try again.");
            Changed?.Invoke();
        }
        return false;
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
        if (msg == WM_CLIPBOARDUPDATE && !_disposed)
        {
            int generation = ++_generation;
            uint sequence = _sequenceNumber();
            // Never perform clipboard conversion or hashing inside a native callback.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = CaptureAsync(generation, sequence)));
        }
        return IntPtr.Zero;
    }

    private async Task CaptureAsync(int generation, uint sequence)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_disposed || !IsListening || generation != _generation || _sequenceNumber() != sequence) return;
            try
            {
                if (Clipboard.GetData(OriginFormat) as string == _origin) return;
                IslandClipboardItem? item = null;
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    if ((long)image.PixelWidth * image.PixelHeight * 4 > MemoryBudgetBytes) { RejectOversized(); return; }
                    if (!image.IsFrozen)
                    {
                        image = BitmapFrame.Create(image);
                        image.Freeze();
                    }
                    var preview = MakePreview(image);
                    string hash = await Task.Run(() => ImageHash(image));
                    item = new IslandClipboardItem { Kind = IslandClipboardKind.Image, Image = image, Preview = preview, ContentHash = hash };
                }
                else if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if ((long)text.Length * sizeof(char) > MemoryBudgetBytes) { RejectOversized(); return; }
                    if (!string.IsNullOrWhiteSpace(text)) item = new IslandClipboardItem { Kind = IslandClipboardKind.Text, Text = text };
                }
                if (!_disposed && generation == _generation && item != null) Add(item);
                return;
            }
            catch (COMException) when (attempt < 2) { await _retryDelay(attempt == 0 ? 50 : 150); }
            catch (Exception ex)
            {
                Log.Warn(ex, "Island clipboard capture failed");
                if (!_disposed && generation == _generation)
                {
                    Error = IslandStrings.Get("IslandClipboardBusy", "The clipboard is busy. Try again.");
                    Changed?.Invoke();
                }
                return;
            }
        }
    }

    internal static string ImageHash(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        byte[] row = new byte[stride];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(image.PixelWidth));
        hash.AppendData(BitConverter.GetBytes(image.PixelHeight));
        for (int y = 0; y < image.PixelHeight; y++)
        {
            image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
            hash.AppendData(row);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void RejectOversized()
    {
        Error = IslandStrings.Get("IslandClipboardTooLarge", "This item exceeds the 128 MiB history limit and was not saved.");
        Changed?.Invoke();
    }

    private void Add(IslandClipboardItem item)
    {
        if (item.MemoryBytes > MemoryBudgetBytes) { RejectOversized(); return; }
        if (_items.Count > 0 && _items[0].SameAs(item)) return;
        _items.Insert(0, item);
        _memoryBytes += item.MemoryBytes;
        Trim();
        Error = "";
        Changed?.Invoke();
    }

    public void Trim()
    {
        while (_items.Count > 0 && (_items.Count > Math.Clamp(MaxItems, 1, 100) || _memoryBytes > MemoryBudgetBytes))
        {
            _memoryBytes -= _items[^1].MemoryBytes;
            _items.RemoveAt(_items.Count - 1);
        }
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
