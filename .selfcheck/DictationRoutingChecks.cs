using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;

internal static class DictationRoutingChecks
{
    internal static void Run()
    {
        foreach (int[] order in new[] { new[] { 0xA2, 0xA0 }, new[] { 0xA0, 0xA2 }, new[] { 0xA3, 0xA1 } })
        {
            foreach (bool foreign in new[] { false, true })
            foreach (int accepted in new[] { 0, 1, 2 })
            {
                var router = new DictationKeyRouter();
                var delivered = new HashSet<int>();
                int completed = 0;
                void Key(int vk, bool down, bool enabled = true)
                {
                    var result = router.Route(new(vk, 0, false, down), [0x11, 0x10], enabled, true);
                    if (result.Completed) completed++;
                    if (!result.Consume) Deliver(vk, down);
                    if (result.Replay is { } replay)
                    {
                        for (int i = 0; i < accepted; i++) Deliver(replay[i].VirtualKey, replay[i].Down);
                        router.ReplayAccepted(replay, accepted);
                    }
                }
                void Deliver(int vk, bool down)
                {
                    if (down) delivered.Add(vk);
                    else if (!delivered.Remove(vk)) throw new Exception("Delivered an unmatched key up");
                }
                Key(order[0], true); Key(order[1], true); Key(order[1], true); // repeat completing modifier
                if (foreign) { Key(0x43, true); Key(0x43, false); }
                Key(order[0], false, false); Key(order[1], false, false); // disable/change settings while captured
                if (delivered.Count != 0 || router.InputHeld || completed != 1)
                    throw new Exception("Gesture ledger failed");
            }
        }
        var escape = new DictationKeyRouter();
        var cancel = escape.Route(new(0x1B, 0, false, true), [0x11, 0x10], true, true);
        if (!cancel.Cancel || !cancel.Consume
            || escape.Route(new(0x1B, 0, false, true), [], false, true).Cancel
            || !escape.Route(new(0x1B, 0, false, false), [], false, false).Consume)
            throw new Exception("Escape ownership failed");
        var sides = new DictationKeyRouter();
        sides.Route(new(0xA2, 0, false, true), [0x11, 0x10], true, false);
        sides.Route(new(0xA3, 0, false, true), [0x11, 0x10], true, false);
        sides.Route(new(0xA0, 0, false, true), [0x11, 0x10], true, false);
        sides.Route(new(0xA2, 0, false, false), [0x11, 0x10], true, false);
        if (!sides.IsDown(0x11)) throw new Exception("Releasing one side lost the other modifier");
        sides.Disarm();
        sides.ObservePhysical(0xA3, false); sides.ObservePhysical(0xA0, false);
        if (sides.InputHeld) throw new Exception("Confirmed physical release was not reconciled");
        var observer = new FluentFlyoutWPF.Classes.KeyboardReconciler();
        observer.Hook(0xA2, true, 1000); observer.Raw(0xA2, false, 1100);
        observer.Hook(0xA0, true, 1000); observer.Raw(0xA0, false, 1100);
        if (observer.Missing(1200).Count != 0 || observer.Missing(1400).Count != 2)
            throw new Exception("Missing releases must reconcile all modifiers after the bounded ordering window");
        observer.Raw(0xA2, false, 2000); observer.Hook(0xA2, false, 2100);
        observer.Hook(0xA0, false, 2000); observer.Raw(0xA0, false, 2100);
        if (observer.Missing(2400).Count != 0) throw new Exception("Delivery order incorrectly triggered recovery");
        observer.Raw(0xA2, true, 3000); observer.Clear();
        if (observer.Missing(4000).Count != 0) throw new Exception("Desktop reset retained an unconfirmed observation");
        Console.WriteLine("Dictation routing checks passed: key ledgers, both orders/sides, repeats, Escape, partial/denied replay, disarm.");
    }
}
