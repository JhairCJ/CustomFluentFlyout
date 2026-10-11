using FluentFlyoutWPF.Classes;

internal static class AttachmentChecks
{
    internal static void Run()
    {
        var native = new FakeNative();
        var attachment = new TaskbarAttachment(native);
        if (attachment.Attach(1, 0) || attachment.Attach(0, 2)) throw new Exception("Invalid windows were attached");
        if (!attachment.Attach(1, 2) || native.WindowStyle != 0x40000000 || native.Changes != 1)
            throw new Exception("The official WS_CHILD/SetParent contract failed");
        if (attachment.Attach(1, 2) || native.Changes != 1) throw new Exception("Attachment is not idempotent");
        if (!attachment.Attach(1, 3) || native.Changes != 2) throw new Exception("Replacement taskbar was not attached once");
        foreach (var scale in new[] { 1.0, 1.25, 2.0 })
        {
            int x = -1920 + (int)(40 * scale), y = -1080 + (int)(20 * scale);
            if (attachment.ClientPoint(3, x, y) != ((int)(40 * scale), (int)(20 * scale)))
                throw new Exception("Physical screen/client conversion failed for negative origin and mixed DPI");
        }
        native.Valid = false;
        if (attachment.Attach(1, 3)) throw new Exception("Destroyed native child was reused");
        Console.WriteLine("Official taskbar attachment checks passed; no shell changes.");
    }
    private sealed class FakeNative : ITaskbarAttachmentNative
    {
        internal bool Valid = true;
        internal int WindowStyle = unchecked((int)0x80000000), Changes;
        private IntPtr _parent;
        public bool IsWindow(IntPtr window) => Valid && window != IntPtr.Zero;
        public IntPtr Parent(IntPtr window) => _parent;
        public int Style(IntPtr window) => WindowStyle;
        public void Style(IntPtr window, int style) => WindowStyle = style;
        public void Parent(IntPtr window, IntPtr parent) { _parent = parent; Changes++; }
        public (int X, int Y) ClientPoint(IntPtr parent, int x, int y) => (x + 1920, y + 1080);
    }
}
