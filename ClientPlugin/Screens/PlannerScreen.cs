using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// Opened with Ctrl+N anywhere in a world or from the plugin's config button in Pulsar.
/// In the G menu + adds blocks directly; in the window every change is a button.
/// Every change to the list is made here with buttons, never with stray keys. Texts come from Policy/Tr (13 languages).
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
    private Button check, orderAll, orderMissing, clearList;
    /// <summary>Set when an order button is clicked: the order buttons stay off until the game thread has answered
    /// (a newer check result) or 3 s have passed, so a second click cannot order everything twice.</summary>
    private DateTime waitingSince = DateTime.MinValue;
    private DateTime viewBeforeClick;
    private string lastDrawn;
    /// <summary>Settings "Confirm clearing": the button ("clear" or "stop") waiting for its second click, and since when.</summary>
    private string armed;
    private DateTime armedAt;

    /// <summary>Opens the window (UI thread). Does nothing when it is already open.</summary>
    public static void Open()
    {
        if (open) return;
        try
        {
            GameLanguage.Refresh(); // the window is built in the game's language
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
            Text = "LW Build Planner", FontSize = 21, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "X", Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Dispose();
        // Settings: the window closes and the settings dialog opens (Ctrl+N brings the window back with the new settings).
        var settings = new Button
        {
            Content = Tr.T("btn.settings"), Height = 30, MinHeight = 0, Padding = new Thickness(10, 0, 10, 0), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(settings, SmallTip(Tr.T("tip.settings")));
        settings.Click += (_, _) =>
        {
            PluginLog.Info("Window: 'Settings' clicked");
            Dispose();
            Dispatcher.UIThread.Post(() => Plugin.Instance?.OpenConfigDialog(), DispatcherPriority.Background);
        };
        var titleBar = new Grid();
        titleBar.Children.Add(title);
        titleBar.Children.Add(settings);
        titleBar.Children.Add(close);

        content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(12, 8, 12, 8) };
        scroll = new ScrollViewer
        {
            Content = content, Margin = new Thickness(0, 12, 0, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(96, 41, 54, 62)),
        };

        // WrapPanel: longer button texts (other languages) go on to a second row instead of leaving the window.
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        check = MakeButton(Tr.T("btn.check"), Tr.T("tip.check"), () => PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None)));
        buttons.Children.Add(check);
        orderAll = MakeButton(Tr.T("btn.orderAll"), Tr.T("tip.orderAll"), () => Order(OrderMode.All));
        buttons.Children.Add(orderAll);
        orderMissing = MakeButton(Tr.T("btn.orderMissing"), Tr.T("tip.orderMissing"), () => Order(OrderMode.Missing));
        buttons.Children.Add(orderMissing);
        clearList = MakeButton(Tr.T("btn.clearList"), Tr.T("tip.clearList"), () =>
        {
            if (!Confirmed("clear")) return;
            PlanStore.Clear();
            PlannerEngine.Enqueue(c =>
            {
                // A new list starts clean: finished orders of the old list go too. Running orders stay (stop them in their box).
                if (!OrderBook.LedgerForCheck.Active) OrderBook.Reset();
                PlanService.Check(c, OrderMode.None, quiet: true);
            });
        });
        buttons.Children.Add(clearList);
        // Log every press that reaches a button of the bar (also grey ones, which raise no Click): a press that is not
        // in the log never reached the button (something lay over it).
        buttons.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            foreach (var child in buttons.Children)
            {
                if (child is not Button button) continue;
                var at = e.GetPosition(button);
                if (at.X >= 0 && at.Y >= 0 && at.X <= button.Bounds.Width && at.Y <= button.Bounds.Height)
                    PluginLog.Info($"Window: '{button.Content}' pressed{(button.IsEnabled ? "" : " while off")}");
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
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
            // Up to 900 x 760 and never larger than the screen (40 px free on every side): the game scales its UI with
            // the screen height, so this is the same share of the screen at 1080p and 1440p and still fits on smaller ones.
            MaxWidth = 900, MaxHeight = 760, Margin = new Thickness(40),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
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
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; // answers within a quarter second
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

    /// <summary>Order all / Order missing: both order buttons go off at once until the game thread has answered.</summary>
    private void Order(OrderMode mode)
    {
        // Order all while orders run would make everything twice: say so instead of ordering (the button stays
        // clickable, so a click always gets an answer). Problems are answered by the check itself.
        if (mode == OrderMode.All && OrderBook.Lines.Any(o => o.State != OrderState.Done && o.State != OrderState.Stopped))
        {
            PlanStore.Say(Tr.T("hud.ordersRunning"));
            return;
        }
        // A second order click before the answer came back (at most 3 s) is ignored, so a quick double click cannot
        // order everything twice. The buttons are not greyed out for this: switching a button off inside its own
        // click can leave it "pressed" in Avalonia, and then the next click is lost.
        var view = PlanService.LastView;
        var answered = view != null && view.Time > viewBeforeClick;
        if (!answered && DateTime.Now - waitingSince < TimeSpan.FromSeconds(3))
        {
            PluginLog.Info($"Window: order {mode} ignored, the previous order is still being answered");
            return;
        }
        PluginLog.Info($"Window: order {mode} sent to the game thread");
        waitingSince = DateTime.Now;
        viewBeforeClick = view?.Time ?? DateTime.MinValue;
        PlannerEngine.Enqueue(c => PlanService.Check(c, mode));
    }

    /// <summary>
    /// Settings "Confirm clearing" (off by default): the first click only arms the button (its text asks for a second
    /// click), a second click within 4 s does it. With the setting off every click counts at once.
    /// </summary>
    private bool Confirmed(string which)
    {
        if (!PlannerSettings.Current.ConfirmClear) return true;
        if (armed == which && DateTime.Now - armedAt <= TimeSpan.FromSeconds(4))
        {
            armed = null;
            return true;
        }
        armed = which;
        armedAt = DateTime.Now;
        PluginLog.Info($"Window: '{which}' waits for a second click");
        return false;
    }

    private Button MakeButton(string text, string tip, Action run)
    {
        // MinHeight = 0: the game's button style has a minimum height of its own, so a smaller Height would be ignored.
        var button = new Button { Content = text, Height = 34, MinHeight = 0, Padding = new Thickness(12, 2, 12, 2), FontSize = 15, Margin = new Thickness(3) };
        ToolTip.SetTip(button, SmallTip(tip));
        // The buttons sit at the bottom of the screen: a tooltip placed below has no room there and pops up over the
        // mouse, so the first click lands on the tooltip instead of the button. Above the button it never covers it.
        ToolTip.SetPlacement(button, PlacementMode.Top);
        ToolTip.SetVerticalOffset(button, -6);
        ToolTip.SetShowDelay(button, 700);
        button.Click += (_, _) =>
        {
            try
            {
                PluginLog.Info($"Window: '{text}' clicked");
                run();
                lastDrawn = null; // show the result as soon as it is there
                Dispatcher.UIThread.Post(Redraw, DispatcherPriority.Background); // after the click, never inside it
            }
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
            var view = PlanService.LastView;
            var orders = OrderBook.Lines;
            // Order all / Order missing are grey while the last check says they are blocked (short of ore, no space, ...):
            // "Order missing" stays open when storage covers what "Order all" lacks. Check (or any list change) checks
            // again, e.g. after mining. Every click still checks storage first (Order() ignores a click while the
            // previous one is answered), and running orders are answered with a notification.
            var allOk = view == null || view.CanBuild;
            orderAll.IsEnabled = plan.Count > 0 && allOk;
            orderMissing.IsEnabled = plan.Count > 0 && (allOk || view.MissingOk);
            clearList.IsEnabled = plan.Count > 0;
            check.IsEnabled = plan.Count > 0; // nothing to check in an empty list
            if (armed != null && DateTime.Now - armedAt > TimeSpan.FromSeconds(4))
            {
                armed = null; // the second click did not come: back to normal
                lastDrawn = null;
            }
            var clearText = armed == "clear" ? Tr.T("btn.confirm") : Tr.T("btn.clearList");
            if (!Equals(clearList.Content, clearText)) clearList.Content = clearText;

            // Rebuild only when something changed, so hovering and scrolling are not disturbed every second.
            var project = PlanStore.ProjectName;
            var stamp = string.Join("|", plan.Select(p => p.Name + p.Count + (p.Project ? "p" : ""))) + "#" + project + "#" + armed + "#" + view?.Time.Ticks + "#"
                        + string.Join("|", orders.Select(o => $"{o.Item}{o.Done}/{o.Ordered}/{o.Waiting}/{o.Queued}/{o.State}{o.Detail}{o.Note}"));
            if (stamp == lastDrawn) return;
            lastDrawn = stamp;

            var offset = scroll.Offset;
            content.Children.Clear();
            elapsedText = null;

            // 1. Build list
            var list = Card(Tr.T("card.list"), Tr.T("hint.list", PlanLimits.Max(true), PlanLimits.Max(false)));
            if (plan.Count == 0) Line(list, Tr.T("empty"), Dim);
            foreach (var entry in plan.Where(p => !p.Project)) list.Children.Add(ListRow(entry));
            // The blueprint project (one at a time): a heading with its name, size and an X for the whole blueprint,
            // then its lines (no line limit). Hand-picked lines stay above it.
            var projectLines = plan.Where(p => p.Project).ToList();
            if (projectLines.Count > 0)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 2) };
                head.Children.Add(new TextBlock
                {
                    Text = Tr.T("list.project", project ?? "?", N(projectLines.Sum(p => (long)p.Count)), projectLines.Count),
                    FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Head, VerticalAlignment = VerticalAlignment.Center,
                });
                var dropArmed = armed == "project";
                var drop = new Button
                {
                    Content = dropArmed ? Tr.T("btn.confirm") : "X", Height = 24, MinWidth = 0, MinHeight = 0, FontSize = 13,
                    Width = dropArmed ? double.NaN : 26, Padding = dropArmed ? new Thickness(8, 0, 8, 0) : new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(drop, SmallTip(Tr.T("tip.removeProject")));
                drop.Click += (_, _) =>
                {
                    try
                    {
                        PluginLog.Info("Window: 'remove blueprint' clicked");
                        if (Confirmed("project"))
                        {
                            PlanStore.RemoveProject();
                            PlanService.Refresh();
                        }
                        lastDrawn = null;
                        Dispatcher.UIThread.Post(Redraw, DispatcherPriority.Background); // rebuilt after its click, not inside it
                    }
                    catch (Exception e) { PluginLog.Error("Remove blueprint failed: " + e); }
                };
                head.Children.Add(drop);
                list.Children.Add(head);
                foreach (var entry in projectLines) list.Children.Add(ListRow(entry));
            }

            // 2. Parts
            var parts = Card(Tr.T("card.parts") + (view == null ? "" : $"   ({Tr.T("parts.checked", view.Time.ToString("HH:mm:ss"))}{(string.IsNullOrEmpty(view.Grid) ? "" : ", " + view.Grid)})"),
                Tr.T("hint.parts"));
            if (view == null) Line(parts, Tr.T("checking"), Dim);
            else if (view.Parts.Count == 0) Line(parts, Tr.T("nothingToCheck"), Dim);
            else
            {
                parts.Children.Add(Row(Dim, (Tr.T("col.part"), 280), (Tr.T("col.need"), 100), (Tr.T("col.stock"), 110), (Tr.T("col.onOrder"), 100), (Tr.T("col.missing"), 100)));
                foreach (var part in view.Parts)
                    parts.Children.Add(Row(part.Missing > 0 ? Warn : Good, (part.Item, 280), (N(part.Need), 100), (N(part.Stock), 110),
                        (N(part.Incoming), 100), (N(part.Missing), 100)));
                if (view.Parts.All(p => p.Missing == 0)) Line(parts, Tr.T("parts.haveAll"), Good);
            }

            // 3. Problems
            if (view != null && view.Alerts.Count > 0)
            {
                var problems = Card(view.CanBuild ? Tr.T("card.notes") : Tr.T("card.problems"),
                    view.CanBuild ? null : Tr.T("hint.problems"));
                foreach (var alert in view.Alerts)
                    Line(problems, alert.Text, alert.Level == AlertLevel.Error ? Bad : Warn);
                if (view.MissingNote != null) Line(problems, view.MissingNote, view.MissingOk ? Good : Bad);
            }

            // 4. Orders: progress per part, oldest order first
            var card = Card(Tr.T("card.orders"), Tr.T("hint.orders"));
            if (orders.Count == 0) Line(card, Tr.T("orders.none"), Dim);
            else
            {
                // The orders' own button sits in their box, so it cannot be taken for one of the list's buttons.
                // Running orders: "Stop orders". All of them finished (or stopped): the same place says "Remove orders".
                var over = orders.All(o => o.State == OrderState.Done || o.State == OrderState.Stopped);
                var stopText = over ? Tr.T("btn.removeOrders") : armed == "stop" ? Tr.T("btn.confirm") : Tr.T("btn.stopOrders");
                var stop = MakeButton(stopText, Tr.T(over ? "tip.removeOrders" : "tip.stopOrders"), () =>
                {
                    if (!over && !Confirmed("stop")) return; // removing finished orders needs no confirmation
                    PlannerEngine.Enqueue(_ => OrderBook.Reset());
                });
                stop.HorizontalAlignment = HorizontalAlignment.Right;
                stop.Height = 28;
                stop.FontSize = 14;
                card.Children.Add(stop);
                var ordered = orders.Sum(o => o.Ordered);
                var done = orders.Sum(o => o.Done);
                var finished = orders.Count(o => o.State == OrderState.Done);
                var allDone = finished == orders.Count;
                var headLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 4) };
                headLine.Children.Add(new TextBlock
                {
                    Text = allDone ? Tr.T("orders.allDone", N(done))
                                   : Tr.T("orders.progress", finished, orders.Count, N(done), N(ordered), ordered > 0 ? 100 * done / ordered : 0),
                    FontSize = 16, Foreground = allDone ? Good : Brushes.White, FontWeight = FontWeight.Bold,
                });
                elapsedText = new TextBlock { Text = OrderBook.ElapsedLabel(), FontSize = 14, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right };
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
            OrderState.Done => (Tr.T("state.done"), Good),
            OrderState.Crafting => (Tr.T("state.crafting"), Busy),
            OrderState.Queued => (Tr.T("state.queued"), Warn),
            OrderState.WaitingForCrafter => (Tr.T("state.waitCrafter"), Warn),
            OrderState.NoProgress => (Tr.T("state.noProgress"), Bad),
            _ => (Tr.T("state.stopped"), Dim),
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("30,230,170,140,*"), Margin = new Thickness(0, 5, 0, 0) };
        void Cell(Control control, int column)
        {
            Grid.SetColumn(control, column);
            row.Children.Add(control);
        }
        Cell(new TextBlock { Text = order.Position + ".", FontSize = 15, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }, 0);
        Cell(new TextBlock
        {
            Text = order.Item, FontSize = 15, Foreground = order.State == OrderState.Done ? Good : Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        }, 1);
        Cell(Bar(order.Ordered > 0 ? (double)order.Done / order.Ordered : 0, order.State == OrderState.Done ? Good : brush, 10), 2);
        Cell(new TextBlock
        {
            Text = $"{N(order.Done)} / {N(order.Ordered)}", FontSize = 15, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        }, 3);
        Cell(new TextBlock { Text = label, FontSize = 15, Foreground = brush, VerticalAlignment = VerticalAlignment.Center }, 4);

        var box = new StackPanel { Spacing = 1 };
        box.Children.Add(row);
        var detail = string.Join("   ", new[] { order.Detail, order.Note }.Where(t => !string.IsNullOrEmpty(t)));
        if (order.Waiting > 0) detail = (detail.Length > 0 ? detail + "   " : "") + Tr.T("orders.notQueued", N(order.Waiting));
        if (detail.Length > 0)
            box.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 0, 0, 0),
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

    /// <summary>One build list line: name, [-] count [+] [X]. Shift and Ctrl give the bigger steps.</summary>
    private StackPanel ListRow(PlannedEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new TextBlock
        {
            Text = entry.Name, Width = 380, FontSize = 15, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(StepButton("-", Tr.T("tip.minus"), entry, -1));
        row.Children.Add(new TextBlock
        {
            Text = entry.Count.ToString("N0"), Width = 52, FontSize = 15, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(StepButton("+", Tr.T("tip.plus"), entry, +1));
        row.Children.Add(StepButton("X", Tr.T("tip.remove"), entry, 0));
        return row;
    }

    private Button StepButton(string text, string tip, PlannedEntry entry, int sign)
    {
        var button = new Button
        {
            Content = text, Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, SmallTip(tip));
        button.Click += (_, _) =>
        {
            try
            {
                if (sign == 0) PlanStore.Remove(entry.Key);
                else
                {
                    var (shift, ctrl, alt) = GPick.Modifiers();
                    PlanStore.Change(entry.Key, sign * PlanLimits.Step(entry.Armor, shift, ctrl, alt));
                }
                PlanService.Refresh(); // the checks follow the new list (quick clicks: one check)
                lastDrawn = null;
                Dispatcher.UIThread.Post(Redraw, DispatcherPriority.Background); // the row is rebuilt after its click, not inside it
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
        inner.Children.Add(new TextBlock { Text = title, FontSize = 18, Foreground = Head, FontWeight = FontWeight.Bold });
        if (!string.IsNullOrEmpty(hint))
            inner.Children.Add(new TextBlock { Text = hint, FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
        content.Children.Add(new Border
        {
            Child = inner, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 8, 12, 10),
            CornerRadius = new CornerRadius(6), Background = CardBack, BorderBrush = CardEdge, BorderThickness = new Thickness(1),
        });
        return inner;
    }

    /// <summary>Tooltips are short hints in a small font, so they never cover much of the window.</summary>
    private static TextBlock SmallTip(string text)
        => new() { Text = text, FontSize = 13, MaxWidth = 320, TextWrapping = TextWrapping.Wrap };

    private static void Line(Panel panel, string text, IBrush brush)
        => panel.Children.Add(new TextBlock { Text = text, FontSize = 15, Foreground = brush, TextWrapping = TextWrapping.Wrap });

    private static StackPanel Row(IBrush brush, params (string Text, double Width)[] cells)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < cells.Length; i++)
            row.Children.Add(new TextBlock
            {
                Text = cells[i].Text, Width = cells[i].Width, FontSize = 15, Foreground = brush,
                TextAlignment = i == 0 || cells[i].Width > 200 ? TextAlignment.Left : TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
            });
        return row;
    }
}
