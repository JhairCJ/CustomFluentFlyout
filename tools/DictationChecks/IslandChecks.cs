using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using FluentFlyoutWPF.Classes;
using Wpf.Ui.Controls;

// Opt-in checks: no MainWindow, device watcher, player commands or user settings.
internal static class IslandChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Run()
    {
        CheckBluetoothUpdates();
        CheckBluetoothLayout();
        CheckInactiveMotion();
        Check(IslandNoticePolicy.Arms(true, false, false), "Bluetooth notice did not expire in active mode");
        Check(IslandNoticePolicy.OnExpired(true, false, false, true, true, false) == IslandNoticeStep.Retry,
            "A temporary notice interrupted the expanded view");
        Check(IslandNoticePolicy.OnExpired(true, false, false, true, false, false) == IslandNoticeStep.Retract,
            "A forced notice remained after returning to compact mode");
        Console.WriteLine("Island checks passed: Bluetooth updates, native compact layout, settled motion and notice policy.");
    }

    private static void CheckBluetoothUpdates()
    {
        using var monitor = new IslandBluetoothMonitor();
        var type = typeof(IslandBluetoothMonitor);
        var apply = type.GetMethod("ApplyUpdate", Private)!;
        var names = (IDictionary<string, string>)type.GetField("_names", Private)!.GetValue(monitor)!;
        var connected = (IDictionary<string, bool>)type.GetField("_connected", Private)!.GetValue(monitor)!;
        var charging = (IDictionary<string, bool?>)type.GetField("_charging", Private)!.GetValue(monitor)!;
        var notices = new List<string>();
        IslandBluetoothDevice? latest = null;
        monitor.Connected += device => { latest = device; notices.Add("connected"); };
        monitor.Updated += device => { latest = device; notices.Add("updated"); };
        monitor.Disconnected += _ => notices.Add("disconnected");
        names["headset"] = "";
        type.GetField("_seeding", Private)!.SetValue(monitor, false);
        void Update(Dictionary<string, object> properties) => apply.Invoke(monitor, ["headset", properties]);

        Update(new() { ["System.Devices.Aep.IsConnected"] = true, ["System.Devices.BatteryLife"] = 80 });
        Check(notices.SequenceEqual(["connected"]) && connected["headset"], "Connection was not published once");
        notices.Clear();
        Update(new()
        {
            ["System.ItemNameDisplay"] = "Sony WH-1000XM5",
            ["System.Devices.Aep.Bluetooth.Cod.Services.Audio"] = true,
            ["System.Devices.ChargingState"] = 1,
        });
        Check(notices.SequenceEqual(["updated"]) && latest is { Name: "Sony WH-1000XM5", Charging: true }
            && latest.Glyph == SymbolRegular.Headphones24, "Late device identity did not refine the visible notice");
        notices.Clear();
        Update(new()
        {
            ["System.Devices.Aep.IsConnected"] = false,
            ["System.Devices.BatteryLife"] = 79,
            ["System.Devices.ChargingState"] = 0,
        });
        Check(notices.SequenceEqual(["updated", "disconnected"]) && !connected["headset"]
            && latest is { BatteryPercent: 79, Charging: false }, "Battery data swallowed a disconnect");
        notices.Clear();
        Update(new() { ["System.Devices.BatteryLife"] = 78 });
        Check(notices.Count == 0 && !connected["headset"], "Battery refinement revived a disconnected device");

        var clean = type.GetMethod("CleanName", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((string)clean.Invoke(null, ["ACE"])! == "ACE", "A short device name was mistaken for an address");
        Check((string)clean.Invoke(null, ["AA:BB:CC:DD:EE:FF"])! == "", "A MAC address was shown as a name");
        monitor.Stop();
        Check(names.Count == 0 && charging.Count == 0 && connected.Count == 0, "Stopping retained stale device state");
    }

    private static void CheckBluetoothLayout()
    {
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load("FluentFlyoutWPF/Windows/IslandWindow.xaml");
        var source = document.Descendants(w + "Grid").Single(e => (string?)e.Attribute(x + "Name") == "BluetoothCompactGrid");
        foreach (double limit in new[] { 180.0, 200.0, 240.0 })
        foreach (bool battery in new[] { false, true })
        foreach (string deviceName in new[] { "ACE", "AirPods Pro", new string('W', 80) })
        {
            var gridSource = new XElement(source);
            foreach (var icon in gridSource.Descendants().Where(e => e.Name.LocalName == "SymbolIcon").ToArray())
                icon.ReplaceWith(new XElement(w + "Border", new XAttribute("Width", 15), new XAttribute("Height", 15),
                    new XAttribute("Grid.Column", 0)));
            var grid = (Grid)XamlReader.Parse(gridSource.ToString());
            grid.Visibility = Visibility.Visible;
            var name = (System.Windows.Controls.TextBlock)grid.FindName("BluetoothName");
            name.Text = deviceName;
            ((Grid)grid.FindName("BluetoothBatteryZone")).Visibility = battery ? Visibility.Visible : Visibility.Collapsed;
            name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = name.DesiredSize.Width - name.Margin.Left - name.Margin.Right;
            double wanted = Math.Ceiling(grid.Margin.Left + grid.Margin.Right + grid.ColumnDefinitions[0].Width.Value
                + name.DesiredSize.Width + (battery ? 18 : 0));
            double width = Math.Clamp(wanted, 72, limit);
            grid.Measure(new Size(width, 36)); grid.Arrange(new Rect(0, 0, width, 36));
            Check(name.Visibility == Visibility.Visible && name.ActualWidth > 0 && name.TextAlignment == TextAlignment.Right,
                "Compact Bluetooth lost the device name");
            Check(name.ActualWidth <= textWidth + Math.Max(0, 72 - wanted) + 2,
                "Compact Bluetooth left a large empty gap beside a short name");
        }
    }

    private static void CheckInactiveMotion()
    {
        foreach (double fps in new[] { 25.0, 30, 60, 120, 144, 240 })
        foreach (double duration in new[] { 0.12, 0.15, 0.21, 0.3, 0.45, 0.7 })
        foreach (double start in new[] { 0.0, 0.37, 1.0 })
        foreach (double target in new[] { 0.0, 1.0 })
        {
            double value = start;
            for (int frame = 0; frame < fps * 2; frame++)
            {
                value = IslandPhysics.MoveTowards(value, target, 1 / fps / duration);
                Check(value >= Math.Min(start, target) && value <= Math.Max(start, target),
                    "The inactive transition overshot its destination");
            }
            Check(value == target, "The inactive transition kept its render loop alive after settling");
            double reverse = 1 - target;
            for (int frame = 0; frame < fps * 2; frame++)
                value = IslandPhysics.MoveTowards(value, reverse, 1 / fps / duration);
            Check(value == reverse, "Reversing the inactive transition did not settle");
        }
    }
}
