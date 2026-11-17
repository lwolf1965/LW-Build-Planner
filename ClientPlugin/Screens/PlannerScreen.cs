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
/// The planner window: build list, parts check (need / stock / on order / missing), problems, and the progress of the
/// running order (per part: done / ordered, status, and what each crafter still has).
/// Opened with Ctrl+N from the G menu (bringing the selected block along) or from the plugin's config button in Pulsar.
/// In the G menu + adds blocks directly; in the window every change is a button.
/// Every change to the list is made here with buttons, never with stray keys.
/// The window only shows snapshots (PlanStore, PlanService.LastView, OrderBook.Lines) and queues actions for the game
/// thread; it never touches the world. Redrawn once a second while open.
/// Layout: Grid with the cards in a ScrollViewer in the * row, so the buttons always stay on screen. The window is
/// redrawn only when its data changed, and the scroll position is put back afterwards, so reading a long order is calm.
/// </summary>
internal class PlannerScreen : ScreenView
{
    private static readonly IBrush Head = new SolidColorBrush(Color.FromRgb(140, 200, 220));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(160, 170, 175));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(255, 110, 100));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(255, 200, 90));
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(130, 220, 140));
    private static readonly IBrush Busy = new SolidColorBrush(Color.FromRgb(110, 185, 255));
    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(130, 70, 82, 92));
    private static readonly IBrush CardBack = new SolidColorBrush(Color.FromArgb(120, 22, 30, 36));
    private static readonly IBrush CardEdge = new SolidColorBrush(Color.FromArgb(150, 70, 96, 116));

    private static bool open;
    private StackPanel content;
    private ScrollViewer scroll;
    private TextBlock elapsedText;
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
        scroll = new ScrollViewer
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
            if (elapsedText != null) elapsedText.Text = OrderBook.ElapsedLabel(); // the clock runs without a rebuild

            var plan = PlanStore.Snapshot().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var offered = PlanStore.Offered;
            var view = PlanService.LastView;
            var orders = OrderBook.Lines;
            // Nothing goes to the crafters while the check finds a problem (ore deficit, no crafter, no room ...).
            orderAll.IsEnabled = plan.Count > 0 && view != null && view.CanBuild;

            // Rebuild only when something changed, so hovering and scrolling are not disturbed every second.
            var stamp = offered?.Name + "#" + string.Join("|", plan.Select(p => p.Name + p.Count)) + "#" + view?.Time.Ticks + "#"
                        + string.Join("|", orders.Select(o => $"{o.Item}{o.Done}/{o.Ordered}/{o.Waiting}/{o.Queued}/{o.State}{o.Detail}{o.Note}"));
            if (stamp == lastDrawn) return;
            lastDrawn = stamp;

            var offset = scroll.Offset;
            content.Children.Clear();
            elapsedText = null;

            // 0. The block brought from the G menu with Ctrl+N
            var pick = Card("Selected block", "The block last selected in the G menu (with + or Ctrl+N). Add more of it with the buttons.");
            if (offered == null) Line(pick, "none yet", Dim);
            else pick.Children.Add(OfferRow(offered, plan.FirstOrDefault(p => ReferenceEquals(p.Key, offered.Key))?.Count ?? 0));

            // 1. Build list
            var list = Card("Build list", "- and + change the count (Shift and Ctrl for bigger steps); a line at 0 leaves the list, X removes it. Limits per line: armor 1000, other blocks 50.");
            if (plan.Count == 0) Line(list, "empty", Dim);
            foreach (var entry in plan) list.Children.Add(ListRow(entry));

            // 2. Parts
            var parts = Card("Parts" + (view == null ? "" : $"   (checked {view.Time:HH:mm:ss}{(string.IsNullOrEmpty(view.Grid) ? "" : ", " + view.Grid)})"),
                "need = what the list uses; in storage = what the crafters can reach now; on order = already being made; missing = need - storage - on order.");
            if (view == null) Line(parts, "checking ...", Dim);
            else if (view.Parts.Count == 0) Line(parts, "nothing to check", Dim);
            else
            {
                parts.Children.Add(Row(Dim, ("part", 300), ("need", 110), ("in storage", 110), ("on order", 110), ("missing", 110)));
                foreach (var part in view.Parts)
                    parts.Children.Add(Row(part.Missing > 0 ? Warn : Good, (part.Item, 300), (N(part.Need), 110), (N(part.Stock), 110),
                        (N(part.Incoming), 110), (N(part.Missing), 110)));
                Line(parts, view.Parts.All(p => p.Missing == 0)
                    ? "You already have every part for this list. 'Order all' makes them again anyway; 'Order missing' would order nothing."
                    : "'Order all' makes the full 'need' column. 'Order missing' makes only the 'missing' column.", Dim);
            }

            // 3. Problems
            if (view != null && view.Alerts.Count > 0)
            {
                var problems = Card(view.CanBuild ? "Notes" : "Problems for 'Order all' (blocked)",
                    view.CanBuild ? null : "Fix these (mine the ore, build the missing crafter, add storage) or make the list smaller with - and the order is allowed again.");
                foreach (var alert in view.Alerts)
                    Line(problems, alert.Text, alert.Level == AlertLevel.Error ? Bad : Warn);
            }

            // 4. Orders: progress per part, oldest order first
            var card = Card("Orders",
                "Parts are handed to the crafters oldest order first, in batches: every free crafter of the right kind gets a batch, and a crafter that is free again gets the next one. "
                + "Build as many crafters as you can, more crafters finish a big build sooner. "
                + "Crafting = a crafter works on it. Waiting = the crafter has it queued but waits for sub-parts from smaller crafters (smelters, refineries) or for earlier jobs. "
                + "Progress counts parts that arrive in storage.");
            if (orders.Count == 0) Line(card, "none. Add blocks with + in the G menu, check the parts above, then press Order all or Order missing.", Dim);
            else
            {
                var ordered = orders.Sum(o => o.Ordered);
                var done = orders.Sum(o => o.Done);
                var finished = orders.Count(o => o.State == OrderState.Done);
                var allDone = finished == orders.Count;
                var headLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 4) };
                headLine.Children.Add(new TextBlock
                {
                    Text = allDone ? $"All {orders.Count} parts finished ({N(done)} made)"
                                   : $"{finished} of {orders.Count} parts finished, {N(done)} of {N(ordered)} made ({(ordered > 0 ? 100 * done / ordered : 0)} %)",
                    FontSize = 18, Foreground = allDone ? Good : Brushes.White, FontWeight = FontWeight.Bold,
                });
                elapsedText = new TextBlock { Text = OrderBook.ElapsedLabel(), FontSize = 17, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(elapsedText, 1);
                headLine.Children.Add(elapsedText);
                card.Children.Add(headLine);
                card.Children.Add(Bar(ordered > 0 ? (double)done / ordered : 0, allDone ? Good : Busy, 16));
                foreach (var order in orders) card.Children.Add(OrderRow(order));
            }

            if (offset.Y > 0) Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Background);
        }
        catch (Exception e)
        {
            PluginLog.Error("Planner window redraw failed: " + e);
            timer?.Stop();
        }
    }

    /// <summary>One ordered part: position, name, progress bar, done / ordered, status; under it what each crafter still has.</summary>
    private static Control OrderRow(OrderLine order)
    {
        var (label, brush) = order.State switch
        {
            OrderState.Done => ("done", Good),
            OrderState.Crafting => ("crafting", Busy),
            OrderState.Queued => ("queued, waiting", Warn),
            OrderState.WaitingForCrafter => ("waiting for a free crafter", Warn),
            OrderState.NoProgress => ("no progress", Bad),
            _ => ("stopped, rest not made", Dim),
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("34,240,190,150,*"), Margin = new Thickness(0, 8, 0, 0) };
        void Cell(Control control, int column)
        {
            Grid.SetColumn(control, column);
            row.Children.Add(control);
        }
        Cell(new TextBlock { Text = order.Position + ".", FontSize = 17, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }, 0);
        Cell(new TextBlock
        {
            Text = order.Item, FontSize = 17, Foreground = order.State == OrderState.Done ? Good : Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        }, 1);
        Cell(Bar(order.Ordered > 0 ? (double)order.Done / order.Ordered : 0, order.State == OrderState.Done ? Good : brush, 10), 2);
        Cell(new TextBlock
        {
            Text = $"{N(order.Done)} / {N(order.Ordered)}", FontSize = 17, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        }, 3);
        Cell(new TextBlock { Text = label, FontSize = 17, Foreground = brush, VerticalAlignment = VerticalAlignment.Center }, 4);

        var box = new StackPanel { Spacing = 1 };
        box.Children.Add(row);
        var detail = string.Join("   ", new[] { order.Detail, order.Note }.Where(t => !string.IsNullOrEmpty(t)));
        if (order.Waiting > 0) detail = (detail.Length > 0 ? detail + "   " : "") + $"{N(order.Waiting)} not queued yet";
        if (detail.Length > 0)
            box.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 15, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 0, 0, 0),
            });
        return box;
    }

    /// <summary>A flat progress bar: a dark track with a coloured fill of the given fraction (0..1).</summary>
    private static Control Bar(double fraction, IBrush fill, double height)
    {
        fraction = Math.Max(0, Math.Min(1, fraction));
        var bar = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 12, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(fraction, GridUnitType.Star)));
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - fraction, GridUnitType.Star)));
        var back = new Border { Background = Track, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumnSpan(back, 2);
        bar.Children.Add(back);
        bar.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) });
        return bar;
    }

    /// <summary>The offered block: name, how many are in the list, and Add buttons with the three step sizes.</summary>
    private StackPanel OfferRow(PlannedEntry offered, int inList)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = offered.Name, Width = 400, FontSize = 18, Foreground = Brushes.White, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(new TextBlock
        {
            Text = inList > 0 ? $"{inList:N0} in list" : "not in list", Width = 110, FontSize = 16, Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var step in new[] { PlanLimits.Step(offered.Armor, false, false), PlanLimits.Step(offered.Armor, true, false), PlanLimits.Step(offered.Armor, false, true) })
        {
            var button = new Button
            {
                Content = "Add " + step, Height = 34, Padding = new Thickness(12, 0, 12, 0), FontSize = 17,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, $"Put {step} more into the build list (at most {PlanLimits.Max(offered.Armor)} per line).");
            button.Click += (_, _) =>
            {
                try
                {
                    var added = PlanStore.AddOffered(offered, step);
                    if (added < step) PlanStore.Say($"Build Planner: {offered.Name} is at the limit of {PlanLimits.Max(offered.Armor)}");
                    PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None));
                    lastDrawn = null;
                    Redraw();
                }
                catch (Exception e) { PluginLog.Error("Planner add button failed: " + e); }
            };
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>One build list line: name, [-] count [+] [X]. Shift and Ctrl give the bigger steps.</summary>
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

    /// <summary>A titled box in the scrolling list; the returned panel takes the rows. The hint explains what the box shows.</summary>
    private StackPanel Card(string title, string hint = null)
    {
        var inner = new StackPanel { Spacing = 3 };
        inner.Children.Add(new TextBlock { Text = title, FontSize = 21, Foreground = Head, FontWeight = FontWeight.Bold });
        if (!string.IsNullOrEmpty(hint))
            inner.Children.Add(new TextBlock { Text = hint, FontSize = 15, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(new Border
        {
            Child = inner, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(16, 10, 16, 14),
            CornerRadius = new CornerRadius(6), Background = CardBack, BorderBrush = CardEdge, BorderThickness = new Thickness(1),
        });
        return inner;
    }

    private static void Line(Panel panel, string text, IBrush brush)
        => panel.Children.Add(new TextBlock { Text = text, FontSize = 17, Foreground = brush, TextWrapping = TextWrapping.Wrap });

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
