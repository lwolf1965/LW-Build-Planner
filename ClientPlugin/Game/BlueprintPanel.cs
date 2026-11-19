using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClientPlugin.Policy;
using HarmonyLib;

namespace ClientPlugin.Game;

/// <summary>
/// The Build Planner box in the game's Blueprint Details screen (the free left column, above the screenshots):
/// before the player decides anything it shows how many blocks the blueprint has, how much ore the whole blueprint
/// costs from scratch, whether it can be built from current stock (else the missing ore), and an "Add to Build
/// Planner" button (two clicks when another blueprint is in the list: it replaces that one).
/// How it gets there (found with the blueprint screen probe, 2026-11-18): the screen's action buttons are a
/// DetailActionsControl (a UserControl) whose StackPanel holds #PART_Place, #PART_ReplaceGridButton, ...; its
/// DataContext is a BlueprintDetailViewModel: _blueprintViewModel is the BlueprintViewModel, _actionHandler
/// (BlueprintActionHandler)._toolbarInputHandler the lifetime the game's own "Place" loads with (often empty until Place
/// was used: then the Blueprints tab's handler is taken). Without any handler (no world, e.g. the main menu) the box stays
/// away. The box goes into the left column's Grid (parent of #ThumbnailsList); the screenshots move down below it.
/// Fail-safe: any error only hides the box (logged); the game's buttons are never touched.
/// </summary>
[HarmonyPatch]
internal static class BlueprintPanel
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("Keen.Game2.Client.UI.Shared.UGC.Detail.DetailActions.DetailActionsControl");
        if (type == null)
        {
            PluginLog.Warn("Blueprint box: DetailActionsControl not found, the Blueprint Details screen gets no box.");
            yield break;
        }
        foreach (var ctor in type.GetConstructors(Flags)) yield return ctor;
    }

    private static void Postfix(object __instance)
    {
        try
        {
            if (__instance is Control control) _ = new Box(control);
        }
        catch (Exception e) { PluginLog.Error("Blueprint box: could not be prepared: " + e); }
    }

    /// <summary>One box, living as long as its DetailActionsControl.</summary>
    private sealed class Box
    {
        private static readonly IBrush Head = new SolidColorBrush(Color.FromRgb(140, 200, 220));
        private static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(160, 170, 175));
        private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(255, 110, 100));
        private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(255, 200, 90));
        private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(130, 220, 140));

        private readonly Control host;
        private readonly Border border;  // the info: in the free left column, else above the button
        private readonly StackPanel content = new() { Spacing = 3 };
        private readonly Button add;
        private Control thumbnails;        // the left column's screenshot list, moved down below the box
        private Thickness thumbnailsMargin;
        private readonly DispatcherTimer timer;

        /// <summary>The blueprint shown (reference of its view model); a new one is read again.</summary>
        private object blueprint;
        private string name;
        private List<PlannedEntry> lines;
        private BlueprintCheck check;
        private string state = "idle"; // idle, reading, failed, checking, shown
        private DateTime checkSince;
        private DateTime armedAt;
        private bool armed;
        private string lastWhy; // the last reason logged for a hidden box, logged again only when it changes

        public Box(Control host)
        {
            this.host = host;
            add = new Button
            {
                Content = Tr.T("btn.addBlueprint"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Height = 44, MinHeight = 0, FontSize = 18,
                Margin = new Thickness(0, 8, 0, 0), IsVisible = false,
            };
            add.Click += (_, e) =>
            {
                e.Handled = true; // our click only: it never travels on to the game's action buttons
                PluginLog.Info("Blueprint box: button clicked");
                Safe(OnAdd);
            };
            border = new Border
            {
                Width = 330, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0), ZIndex = 1,
                Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(150, 22, 30, 36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(170, 70, 96, 116)),
                Child = new StackPanel { Children = { content, add } },
                IsVisible = false,
            };
            border.SizeChanged += (_, _) => Safe(MoveThumbnails);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => Safe(Tick);
            host.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Safe(Attach)); // not while the tree is being attached
            host.DetachedFromVisualTree += (_, _) => Safe(Detach);
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                PluginLog.Error("Blueprint box failed, hidden: " + e);
                try
                {
                    border.IsVisible = false;
                    timer.Stop();
                }
                catch (Exception) { /* nothing more to do */ }
            }
        }

        private void Attach()
        {
            if (host.GetVisualRoot() == null)
            {
                PluginLog.Info("Blueprint box: the screen closed before the box was placed.");
                return;
            }
            if (border.Parent == null)
            {
                // Only the free left column is used: the game's own buttons are never moved or touched (a button put
                // between them made the game's clicks land on Delete / Publish, 2026-11-18).
                var screen = host.GetVisualAncestors().FirstOrDefault(a => a.GetType().Name == "DetailsScreen") as Visual;
                thumbnails = screen?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "ThumbnailsList");
                if (thumbnails?.Parent is not Grid left)
                {
                    thumbnails = null;
                    PluginLog.Warn("Blueprint box: the screen's left column was not found, no box.");
                    return;
                }
                thumbnailsMargin = thumbnails.Margin;
                Grid.SetRow(border, Grid.GetRow(thumbnails));
                Grid.SetColumn(border, Grid.GetColumn(thumbnails));
                Grid.SetRowSpan(border, Grid.GetRowSpan(thumbnails));
                border.Width = Math.Max(300, thumbnails.Bounds.Width > 0 ? thumbnails.Bounds.Width : 330);
                left.Children.Add(border);
                PluginLog.Info("Blueprint box: placed in the left column.");
            }
            blueprint = null; // read again: the stock may have changed since
            timer.Start();
            Tick();
        }

        private void Detach()
        {
            timer.Stop();
            armed = false;
            // The details screen may live on with a new actions control: take the box out of its left column.
            (border.Parent as Panel)?.Children.Remove(border);
            if (thumbnails != null) thumbnails.Margin = thumbnailsMargin;
            thumbnails = null;
        }

        /// <summary>Screenshots of the blueprint stay visible: their list starts below the info.</summary>
        private void MoveThumbnails()
        {
            if (thumbnails == null) return;
            var top = border.IsVisible ? border.Bounds.Height + 12 : 0;
            thumbnails.Margin = new Thickness(thumbnailsMargin.Left, thumbnailsMargin.Top + top, thumbnailsMargin.Right, thumbnailsMargin.Bottom);
        }

        private void Tick()
        {
            BlueprintRead.Poll(); // the terminal hook may not run while this screen is open
            var viewModel = host.DataContext;
            var handler = Value(viewModel, "_actionHandler");
            var lifetime = Value(handler, "_toolbarInputHandler") ?? BlueprintRead.LastLifetime;
            var screen = host.GetVisualAncestors().FirstOrDefault(a => a.GetType().Name == "DetailsScreen") as Control;
            var selected = Value(viewModel, "_blueprintViewModel") ?? Value(handler, "_blueprintViewModel")
                           ?? Value(screen?.DataContext, "ViewModel");
            if (lifetime == null || selected == null)
            {
                Why($"hidden: view model {viewModel?.GetType().Name ?? "none"}, action handler {handler?.GetType().Name ?? "none"}, "
                    + $"load handler {(lifetime == null ? "none" : "found")}, blueprint {(selected == null ? "none" : BlueprintRead.NameOf(selected))}");
                border.IsVisible = false; // no world (main menu) or nothing selected
                blueprint = null;
                MoveThumbnails();
                return;
            }
            Why($"shown for '{BlueprintRead.NameOf(selected)}' (load handler from {(Value(handler, "_toolbarInputHandler") != null ? "the details screen" : "the Blueprints tab")})");
            if (!border.IsVisible)
            {
                border.IsVisible = true;
                MoveThumbnails();
            }
            if (armed && DateTime.Now - armedAt > TimeSpan.FromSeconds(4))
            {
                armed = false;
                Show();
            }
            if (state == "checking" && DateTime.Now - checkSince > TimeSpan.FromSeconds(10))
            {
                state = "shown"; // the world did not answer (paused, loading): show what is known
                check = null;
                Show();
            }
            if (ReferenceEquals(selected, blueprint)) return;

            blueprint = selected;
            name = BlueprintRead.NameOf(selected);
            lines = null;
            check = null;
            armed = false;
            GameLanguage.Refresh();
            var reading = selected;
            state = BlueprintRead.Start(selected, lifetime, found => Safe(() => OnRead(reading, found)),
                () => Safe(() => OnFailed(reading))) ? "reading" : "failed";
            Show();
        }

        private void Why(string why)
        {
            if (why == lastWhy) return;
            lastWhy = why;
            PluginLog.Info("Blueprint box " + why);
        }

        private void OnRead(object which, List<PlannedEntry> found)
        {
            if (!ReferenceEquals(which, blueprint)) return; // another blueprint was opened meanwhile
            lines = found;
            state = "checking";
            checkSince = DateTime.Now;
            Show();
            PlannerEngine.Enqueue(c =>
            {
                BlueprintCheck result;
                try { result = PlanService.Preview(c, found); }
                catch (Exception e)
                {
                    PluginLog.Error("Blueprint check failed: " + e);
                    result = null;
                }
                Dispatcher.UIThread.Post(() => Safe(() =>
                {
                    if (!ReferenceEquals(which, blueprint)) return;
                    check = result;
                    state = "shown";
                    Show();
                }));
            });
        }

        private void OnFailed(object which)
        {
            if (!ReferenceEquals(which, blueprint)) return;
            state = "failed";
            Show();
        }

        private void OnAdd()
        {
            if (lines == null) return;
            var current = PlanStore.ProjectName;
            if (current != null && current != name && PlannerSettings.Current.ConfirmClear && !(armed && DateTime.Now - armedAt <= TimeSpan.FromSeconds(4)))
            {
                armed = true;
                armedAt = DateTime.Now;
                PluginLog.Info($"Blueprint box: replacing '{current}' with '{name}' waits for a second click");
                Show();
                return;
            }
            armed = false;
            PluginLog.Info($"Blueprint box: '{name}' added to the build list" + (current != null && current != name ? $", replacing '{current}'" : ""));
            BlueprintRead.AddProject(name, lines);
            Show();
        }

        /// <summary>Writes the box for the current state.</summary>
        private void Show()
        {
            content.Children.Clear();
            Line("LW Build Planner", Head, 20, bold: true);
            switch (state)
            {
                case "reading":
                    Line(Tr.T("bp.reading"), Dim);
                    break;
                case "failed":
                    Line(Tr.T("bp.failed"), Bad);
                    break;
                default:
                    if (lines == null) break;
                    Line(Tr.T("bp.blocks", N(lines.Sum(l => l.Count)), lines.Count), Brushes.White);
                    if (state == "checking") Line(Tr.T("bp.checking"), Dim);
                    else if (check == null) Line(Tr.T("bp.noCheck"), Warn);
                    else Result(check);
                    break;
            }

            var current = PlanStore.ProjectName;
            add.IsVisible = lines != null;
            add.Content = armed ? Tr.T("btn.replaceBlueprint", Short(current))
                : current == name ? Tr.T("btn.updateBlueprint")
                : Tr.T("btn.addBlueprint");
            add.Foreground = armed ? Warn : Brushes.White;
            if (lines != null && current == name) Line(Tr.T("bp.inPlanner"), Good, 15);
        }

        private void Result(BlueprintCheck result)
        {
            if (result.OreCost.Count > 0)
            {
                Line(Tr.T("bp.oreCost"), Dim, 15);
                Amounts(result.OreCost, Brushes.White, 6);
            }
            if (result.NotMade.Count > 0)
            {
                Line(Tr.T("bp.notMade"), Warn, 15);
                Amounts(result.NotMade, Warn, 3);
            }
            Gap();
            if (result.MissingOre.Count > 0)
            {
                Line(Tr.T("bp.missingOre"), Bad);
                Amounts(result.MissingOre, Bad, 4);
            }
            else if (result.Errors.Count > 0) Line(result.Errors[0], Bad, 15);
            else if (result.CanBuild) Line(Tr.T("bp.canBuild"), Good);
            else if (result.Warnings.Count > 0) Line(result.Warnings[0], Warn, 15);
            if (result.AllShort.Count > 0 && result.MissingOre.Count == 0)
            {
                Line(Tr.T("bp.allShort"), Dim, 15);
                Amounts(result.AllShort, Warn, 3);
            }
        }

        private void Line(string text, IBrush brush, double size = 16, bool bold = false) =>
            content.Children.Add(new TextBlock
            {
                Text = text, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            });

        /// <summary>Ore amounts: one per line (name left, amount right) in the left column, else on one wrapped line.</summary>
        private void Amounts(List<(string Name, long Amount)> items, IBrush brush, int max)
        {
            foreach (var (itemName, amount) in items)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 0, 0) };
                row.Children.Add(new TextBlock { Text = itemName, Foreground = brush, FontSize = 17 });
                var value = new TextBlock { Text = N(amount), Foreground = brush, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                content.Children.Add(row);
            }
        }

        private void Gap() => content.Children.Add(new Border { Height = 4 });

        private static string N(long value) => value.ToString("N0");

        private static string Short(string text) => text == null ? "" : text.Length > 22 ? text.Substring(0, 21) + "…" : text;
    }

    private static object Value(object target, string member)
    {
        if (target == null) return null;
        try { return Reflect.Get(Reflect.Member(target.GetType(), member), target); }
        catch (Exception) { return null; }
    }
}
