using System;
using System.Threading;
using Avalonia.Threading;
using Keen.Game2.Client.UI.HUD.Notification;
using Keen.Game2.Client.UI.InGame;

namespace ClientPlugin.Game;

/// <summary>
/// Shows a text on the game's own HUD notifications. UI thread only.
/// Game API (Game2.Client, public, called directly):
///   HudNotification.CreateTextNotification(ResourceHandle&lt;GUIAsset&gt; icon, LocKey content, NotificationPriority, NotificationType, bool useTimer)
///   InGameUI.ShowNotification(HudNotification)
///   LocKey: our text (already translated by Policy/Tr) goes in through Patches/RawTexts, so the game shows it as it
///   is and logs no "LOCALIZATION WARNING" for it.
/// A message that starts with PlanStore.ImportantMark is shown with NotificationType.Success and NotificationPriority.High
/// (the game may play its notification sound for these, to be checked in game).
/// Any failure switches the HUD off and leaves the messages in the log.
/// The InGameUI comes from the terminal screen (its _inGameUI) and is remembered, so messages also show while no
/// terminal is open (orders placed from the Ctrl+N window in flight): the Session.Update hook asks for a drain on the
/// UI thread whenever messages wait (hub review of PR #41, 2026-11-19: they used to wait for the next G menu).
/// Before the terminal was opened once in a world there is no InGameUI yet; messages wait and are always logged.
/// </summary>
internal static class HudNotify
{
    private static bool unavailable;
    private static WeakReference<InGameUI> lastUi = new(null);
    private static int drainPosted;

    /// <summary>The terminal's InGameUI, remembered for showing messages while no terminal is open. UI thread.</summary>
    public static void Remember(InGameUI inGameUi)
    {
        if (inGameUi != null) lastUi = new WeakReference<InGameUI>(inGameUi);
    }

    /// <summary>A new world: the old InGameUI is not used any more.</summary>
    public static void Forget() => lastUi = new WeakReference<InGameUI>(null);

    /// <summary>Shows up to 3 waiting messages (the rest on the next call). UI thread.</summary>
    public static void Drain(InGameUI inGameUi = null)
    {
        if (inGameUi == null && !lastUi.TryGetTarget(out inGameUi)) return;
        for (var shown = 0; shown < 3 && PlanStore.Messages.TryDequeue(out var message); shown++)
            Show(inGameUi, message);
    }

    /// <summary>Any thread: when messages wait, shows them soon on the UI thread (one post at a time, cheap when idle).</summary>
    public static void PostDrain()
    {
        if (PlanStore.Messages.IsEmpty || Interlocked.Exchange(ref drainPosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref drainPosted, 0);
            try { Drain(); }
            catch (Exception e) { PluginLog.Warn("HUD drain failed: " + e.Message); }
        });
    }

    public static void Show(InGameUI inGameUi, string text)
    {
        if (unavailable || inGameUi == null) return;
        var important = text.Length > 0 && text[0] == PlanStore.ImportantMark;
        if (important) text = text.Substring(1);
        try
        {
            // default icon; useTimer: the message goes away by itself
            var notification = HudNotification.CreateTextNotification(default, Patches.RawTexts.Make(text),
                important ? NotificationPriority.High : NotificationPriority.Normal,
                important ? NotificationType.Success : NotificationType.Info, true);
            inGameUi.ShowNotification(notification);
        }
        catch (Exception e)
        {
            unavailable = true;
            PluginLog.Warn("HUD notification failed, messages stay in the log only: " + e.Message);
        }
    }
}
