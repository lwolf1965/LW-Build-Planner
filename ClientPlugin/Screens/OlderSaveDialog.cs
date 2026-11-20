using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClientPlugin.Game;
using ClientPlugin.Policy;
using ClientPlugin.Tools;
using Keen.VRage.UI.Screens;

namespace ClientPlugin.Screens;

/// <summary>
/// Shown once after loading an older save of a world than the planner's files (Lone 2026-11-20: the HUD line came
/// while the loading screen was still up and was never seen; "maybe create a dialog box"). The player chooses:
/// keep the list as saved, or stop the orders and empty the list (then add the blueprint again and Order missing).
/// </summary>
internal class OlderSaveDialog : ScreenView
{
    private static bool open;

    /// <summary>Opens the dialog (UI thread). Does nothing when it is already open.</summary>
    public static void Open()
    {
        if (open) return;
        try
        {
            GameLanguage.Refresh();
            var sharedUi = GameAccess.GetSharedUI();
            if (sharedUi == null)
            {
                PluginLog.Warn("Older save dialog: the game UI is not available.");
                return;
            }
            sharedUi.CreateScreen<OlderSaveDialog>(new PlannerScreenViewModel(), showCursor: true);
            PluginLog.Info("Older save dialog shown.");
        }
        catch (Exception e)
        {
            PluginLog.Error("Older save dialog failed to open: " + e);
        }
    }

    public OlderSaveDialog()
    {
        var text = new TextBlock
        {
            Text = Tr.T("dlg.olderSave"), FontSize = 17, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 18),
        };
        var keep = DialogButton(Tr.T("btn.keepList"), () => PluginLog.Info("Older save dialog: list kept."));
        var reset = DialogButton(Tr.T("btn.resetList"), () =>
        {
            PluginLog.Info("Older save dialog: orders stopped, list emptied.");
            PlanStore.Clear();
            PlannerEngine.Enqueue(c =>
            {
                OrderBook.Reset();
                PlanService.Check(c, OrderMode.None, quiet: true);
            });
            PlanStore.Say(Tr.T("hud.listReset"));
        });
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(keep);
        buttons.Children.Add(reset);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "LW Build Planner", FontSize = 21, Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 220)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 37, 47, 53)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 200, 90)),
            BorderThickness = new Thickness(1.5),
            MaxWidth = 620, Margin = new Thickness(40),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        root.Children.Add(border);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Transparent;
        Content = root;
    }

    private Button DialogButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label, Height = 34, MinHeight = 0, Padding = new Thickness(14, 0, 14, 0), FontSize = 15,
            Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception e) { PluginLog.Error("Older save dialog button failed: " + e); }
            Dispose();
        };
        return button;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        open = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        open = false;
    }
}
