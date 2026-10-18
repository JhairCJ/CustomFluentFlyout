using System.Runtime.InteropServices;

namespace FluentFlyoutWPF.Classes;

/// <summary>Elapsed time, including sleep, independent of changes to the wall clock.</summary>
internal static class IslandClock
{
    private static readonly DateTime OriginUtc = DateTime.UtcNow;
    private static readonly long OriginMilliseconds = Milliseconds;
    public static long Milliseconds => (long)GetTickCount64();
    // UTC-shaped timestamps for the transient activity registry; never use for calendar dates.
    public static DateTime Now => OriginUtc.AddMilliseconds(Milliseconds - OriginMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
