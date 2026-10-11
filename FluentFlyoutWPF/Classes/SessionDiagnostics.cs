using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DiagnosticStage>))]
internal enum DiagnosticStage { Startup, TaskbarAttach, TaskbarGeometry, Expansion, CaptureOpen, CaptureReady, CaptureStop, CaptureTimeout, CaptureRelease, CaptureError, PreloadStart, PreloadComplete, PreloadFailure, Recognition, RecognitionComplete, RecognitionFailure, Phase, InputRecovery, Shutdown, Fatal, RenderFailure }
internal readonly record struct DiagnosticMilestone(DateTime Utc, DiagnosticStage Stage,
    long Generation, double DurationMs, double X, double Y, double Width, double Height, double Dpi, int Phase, long Sequence);

/// <summary>Fixed metadata and numeric milestones only. Never receives text, audio or paths from dictation.</summary>
internal sealed class BoundedSessionDiagnostics : IDisposable
{
    private readonly object _gate = new(), _fileGate = new();
    private readonly Queue<DiagnosticMilestone> _recent = new(64);
    private readonly long _limit;
    private StreamWriter? _writer;
    private readonly System.Threading.Timer _timer;
    private long _lastWritten, _sequence;
    private readonly string _metadata;
    internal BoundedSessionDiagnostics(string directory, bool software, long limit = 1024 * 1024)
    {
        _limit = limit;
        _metadata = JsonSerializer.Serialize(new
        {
            Version = typeof(App).Assembly.GetName().Version?.ToString(),
            Windows = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), SoftwareRendering = software,
        });
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in new DirectoryInfo(directory).GetFiles("session-*.jsonl")
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(9)) old.Delete();
            _writer = new StreamWriter(new FileStream(Path.Combine(directory,
                $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            _writer.WriteLine(_metadata);
            _writer.Flush();
        }
        catch { try { _writer?.Dispose(); } catch { } _writer = null; }
        _timer = new(_ => Flush(), null, 2000, 2000);
    }
    internal void Mark(DiagnosticStage stage, long generation = 0, double durationMs = 0,
        double x = 0, double y = 0, double width = 0, double height = 0, double dpi = 0, int phase = 0)
    {
        lock (_gate)
        {
            if (_recent.Count == 64) _recent.Dequeue();
            _recent.Enqueue(new(DateTime.UtcNow, stage, generation, durationMs, x, y, width, height, dpi, phase, ++_sequence));
        }
    }
    internal DiagnosticMilestone[] Recent { get { lock (_gate) return _recent.ToArray(); } }
    internal void Flush()
    {
        lock (_fileGate)
        {
            try
            {
                if (_writer == null) return;
                var recent = Recent;
                var pending = recent.Where(entry => entry.Sequence > _lastWritten)
                    .Select(entry => JsonSerializer.Serialize(entry)).ToArray();
                long bytes = pending.Sum(line => (long)System.Text.Encoding.UTF8.GetByteCount(line) + 2);
                if (_writer.BaseStream.Length + bytes > _limit)
                {
                    // Roll the bounded file to the current context instead of losing later failures.
                    _writer.Flush();
                    _writer.BaseStream.SetLength(0);
                    _writer.BaseStream.Position = 0;
                    _writer.WriteLine(_metadata);
                    _writer.Flush();
                    _lastWritten = 0;
                    long available = _limit - _writer.BaseStream.Length;
                    recent = recent.Reverse().TakeWhile(entry =>
                    {
                        available -= System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry)) + 2;
                        return available >= 0;
                    }).Reverse().ToArray();
                }
                foreach (var entry in recent)
                {
                    if (entry.Sequence <= _lastWritten) continue;
                    string line = JsonSerializer.Serialize(entry);
                    if (_writer.BaseStream.Length + System.Text.Encoding.UTF8.GetByteCount(line) + 2 > _limit) break;
                    _writer.WriteLine(line);
                    _writer.Flush();
                    _lastWritten = entry.Sequence;
                }
            }
            catch { try { _writer?.Dispose(); } catch { } _writer = null; }
        }
    }
    public void Dispose()
    {
        _timer.Dispose();
        Flush();
        lock (_fileGate) { _writer?.Dispose(); _writer = null; }
    }
}

internal static class SessionDiagnostics
{
    private static BoundedSessionDiagnostics? _session;
    internal static void Start(bool software) => _session = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentFlyout", "diagnostics"), software);
    internal static void Mark(DiagnosticStage stage, long generation = 0, double durationMs = 0,
        double x = 0, double y = 0, double width = 0, double height = 0, double dpi = 0, int phase = 0)
        => _session?.Mark(stage, generation, durationMs, x, y, width, height, dpi, phase);
    internal static bool IsRenderFailure(Exception? exception) => exception != null
        && (exception.HResult == unchecked((int)0x88980406) || IsRenderFailure(exception.InnerException));
    internal static void Fatal(Exception? exception)
    {
        Mark(IsRenderFailure(exception) ? DiagnosticStage.RenderFailure : DiagnosticStage.Fatal);
        _session?.Flush();
    }
    internal static void Stop() { Mark(DiagnosticStage.Shutdown); _session?.Dispose(); _session = null; }
}
