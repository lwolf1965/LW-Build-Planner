using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClientPlugin.Game;
using ClientPlugin.Policy;
using ClientPlugin.Tools;
using Keen.VRage.UI.Screens;

namespace ClientPlugin.Screens;

/// <summary>
/// The planner window: build list, parts check (need / stock / reserved / incoming / to order), alerts, running orders.
/// Opened with Home from the G menu or from the plugin's config button in Pulsar.
/// The window only shows snapshots (PlanStore, PlanService.LastView, OrderBook.Lines) and queues actions for the game
/// thread; it never touches the world. Redrawn once a second while open.
/// Layout: Grid with the list in a ScrollViewer in the * row, so the buttons always stay on screen.
/// </summary>
internal class PlannerScreen : ScreenView
{
    private static readonly IBrush Head = new SolidColorBrush(Color.FromRgb(140, 200, 220));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(160, 170, 175));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(255, 110, 100));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(255, 200, 90));
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(130, 220, 140));

    private static bool open;
    private StackPanel content;
    private DispatcherTimer timer;
    private Button orderAll;
    private string lastDrawn;

    /// <summary>Opens the window (UI thread). Does nothing when it is already open.</summary>
    public static void Open()
    {
        if (open) return;
        try
        {
            var sharedUi = GameAccess.GetSharedUI();
            if (sharedUi == null)
            {
                PluginLog.Warn("Planner window: the game UI is not available.");
                return;
            }
            PlannerEngine.Enqueue(context => PlanService.Check(context, OrderMode.None));
            sharedUi.CreateScreen<PlannerScreen>(new PlannerScreenViewModel(), showCursor: true);
        }
        catch (Exception e)
        {
            PluginLog.Error("Planner window failed to open: " + e);
        }
    }

    public PlannerScreen()
    {
        var title = new TextBlock
        {
            Text = "LW Build Planner", FontSize = 25, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "X", Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Dispose();
        var titleBar = new Grid();
        titleBar.Children.Add(title);
        titleBar.Children.Add(close);

        content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(12, 8, 12, 8) };
        var scroll = new ScrollViewer
        {
            Content = content, Margin = new Thickness(0, 12, 0, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(96, 41, 54, 62)),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(MakeButton("Check", "Count storage, ore and conveyor links again.", () => PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None))));
        orderAll = MakeButton("Order all", "Make every part of the build list, whatever you already have. The list becomes an order. Blocked while the check below finds a problem.", () => PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.All)));
        buttons.Children.Add(orderAll);
        buttons.Children.Add(MakeButton("Order missing", "Make only the 'missing' column: what storage and running orders do not cover.", () => PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.Missing))));
        buttons.Children.Add(MakeButton("Clear list", "Empty the build list (orders are kept).", () =>
        {
            PlanStore.Clear();
            PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None));
        }));
        buttons.Children.Add(MakeButton("Stop waiting", "Drop parts that are not queued yet. Crafters finish what they have.", () => PlannerEngine.Enqueue(_ => OrderBook.StopWaiting())));
        buttons.Children.Add(MakeButton("Clear orders", "Empty the order list (for lines that make no progress). Crafters keep their queues.", () => PlannerEngine.Enqueue(_ => OrderBook.Reset())));

        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(titleBar);
        layout.Children.Add(scroll);
        layout.Children.Add(buttons);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 37, 47, 53)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 66, 77, 99)),
            BorderThickness = new Thickness(1.5),
            Width = 980, Height = 820,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = layout,
        };
        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        root.Children.Add(border);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Transparent;
        Content = root;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        open = true;
        Redraw();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Redraw();
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer?.Stop();
        timer = null;
        open = false;
        base.OnDetachedFromVisualTree(e);
    }

    private static Button MakeButton(string text, string tip, Action run)
    {
        var button = new Button { Content = text, Height = 44, Padding = new Thickness(14, 4, 14, 4), FontSize = 18 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            try { run(); }
            catch (Exception e) { PluginLog.Error("Planner button failed: " + e); }
        };
        return button;
    }

    private void Redraw()
    {
        try
        {
            var plan = PlanStore.Snapshot().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var view = PlanService.LastView;
            var orders = OrderBook.Lines;
            // Nothing goes to the crafters while the check finds a problem (ore deficit, no crafter, no room ...).
            orderAll.IsEnabled = plan.Count > 0 && view != null && view.CanBuild;

            // Rebuild only when something changed, so hovering and scrolling are not disturbed every second.
            var stamp = string.Join("|", plan.Select(p => p.Name + p.Count)) + "#" + view?.Time.Ticks + "#"
                        + string.Join("|", orders.Select(o => o.Item + o.Waiting + "/" + o.Queued + o.Crafters + o.Note));
            if (stamp == lastDrawn) return;
            lastDrawn = stamp;
            content.Children.Clear();

            Section("Build list   (add blocks in the G menu with =; here - and + change the count, Shift and Ctrl for bigger steps)");
            if (plan.Count == 0) Line("empty", Dim);
            foreach (var entry in plan) content.Children.Add(ListRow(entry));
            if (plan.Count > 0) Line("A line that reaches 0 leaves the list. Limits per line: armor 1000, other blocks 50.", Dim);

            Section("Parts" + (view == null ? "" : $"   (checked {view.Time:HH:mm:ss}{(string.IsNullOrEmpty(view.Grid) ? "" : ", " + view.Grid)})"));
            if (view == null) Line("checking ...", Dim);
            else if (view.Parts.Count == 0) Line("nothing to check", Dim);
            else
            {
                content.Children.Add(Row(Dim, ("part", 300), ("need", 110), ("in storage", 110), ("on order", 110), ("missing", 110)));
                foreach (var part in view.Parts)
                    content.Children.Add(Row(part.Missing > 0 ? Warn : Good, (part.Item, 300), (N(part.Need), 110), (N(part.Stock), 110),
                        (N(part.Incoming), 110), (N(part.Missing), 110)));
                Line(view.Parts.All(p => p.Missing == 0)
                    ? "You already have every part for this list. 'Order all' makes them again anyway; 'Order missing' would order nothing."
                    : "'Order all' makes the full 'need' column. 'Order missing' makes only the 'missing' column.", Dim);
            }

            if (view != null && view.Alerts.Count > 0)
            {
                Section(view.CanBuild ? "Notes" : "Problems for 'Order all' (blocked)");
                foreach (var alert in view.Alerts)
                    Line(alert.Text, alert.Level == AlertLevel.Error ? Bad : Warn);
            }

            Section("Orders");
            if (orders.Count == 0) Line("none", Dim);
            else
            {
                content.Children.Add(Row(Dim, ("part", 250), ("waiting", 92), ("in crafters", 92), ("where / why", 400)));
                foreach (var order in orders)
                    content.Children.Add(Row(order.Waiting > 0 ? Warn : Brushes.White, (order.Item, 250), (N(order.Waiting), 92), (N(order.Queued), 92),
                        (string.IsNullOrEmpty(order.Note) ? order.Crafters : order.Crafters + (order.Crafters.Length > 0 ? "; " : "") + order.Note, 400)));
            }
        }
        catch (Exception e)
        {
            PluginLog.Error("Planner window redraw failed: " + e);
            timer?.Stop();
        }
    }

    /// <summary>One build list line: name, [-] count [+] [X]. The buttons use the same steps as the G menu keys.</summary>
    private StackPanel ListRow(PlannedEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new TextBlock
        {
            Text = entry.Name, Width = 400, FontSize = 17, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(StepButton("-", "One block less. Shift and Ctrl for bigger steps. At 0 the line leaves the list.", entry, -1));
        row.Children.Add(new TextBlock
        {
            Text = entry.Count.ToString("N0"), Width = 70, FontSize = 17, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(StepButton("+", "One block more. Shift and Ctrl for bigger steps. Stops at the limit.", entry, +1));
        row.Children.Add(StepButton("X", "Take the line out of the list.", entry, 0));
        return row;
    }

    private Button StepButton(string text, string tip, PlannedEntry entry, int sign)
    {
        var button = new Button
        {
            Content = text, Width = 40, Height = 34, Padding = new Thickness(0), FontSize = 18,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            try
            {
                if (sign == 0) PlanStore.Remove(entry.Key);
                else
                {
                    var (shift, ctrl) = GPick.Modifiers();
                    PlanStore.Change(entry.Key, sign * PlanLimits.Step(entry.Armor, shift, ctrl));
                }
                PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None)); // the checks follow the new list
                lastDrawn = null;
                Redraw();
            }
            catch (Exception e) { PluginLog.Error("Planner list button failed: " + e); }
        };
        return button;
    }

    private static string N(long value) => value.ToString("N0");

    private void Section(string text)
        => content.Children.Add(new TextBlock { Text = text, FontSize = 20, Foreground = Head, Margin = new Thickness(0, 14, 0, 4) });

    private void Line(string text, IBrush brush)
        => content.Children.Add(new TextBlock { Text = text, FontSize = 17, Foreground = brush, TextWrapping = TextWrapping.Wrap });

    private static StackPanel Row(IBrush brush, params (string Text, double Width)[] cells)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < cells.Length; i++)
            row.Children.Add(new TextBlock
            {
                Text = cells[i].Text, Width = cells[i].Width, FontSize = 17, Foreground = brush,
                TextAlignment = i == 0 || cells[i].Width > 200 ? TextAlignment.Left : TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
            });
        return row;
    }
}
