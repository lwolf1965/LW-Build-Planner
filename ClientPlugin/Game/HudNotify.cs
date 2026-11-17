using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ClientPlugin.Game;

/// <summary>
/// Shows a text on the game's own HUD notifications. UI thread only.
/// Game API (Game2.Client, found by the probe, called through reflection):
///   HudNotification.CreateTextNotification(ResourceHandle&lt;GUIAsset&gt; icon, LocKey content, NotificationPriority, NotificationType, bool useTimer)
///   InGameUI.ShowNotification(HudNotification)
///   LocKey: our text (already translated by Policy/Tr) goes in through Patches/RawTexts, so the game shows it as it
///   is and logs no "LOCALIZATION WARNING" for it.
/// A message that starts with PlanStore.ImportantMark is shown with NotificationType.Success and NotificationPriority.High
/// (enum values looked up by name; the game may play its notification sound for these, to be checked in game).
/// Any failure switches the HUD off and leaves the messages in the log.
/// </summary>
internal static class HudNotify
{
    private static bool unavailable;

    public static void Show(object inGameUi, string text)
    {
        if (unavailable || inGameUi == null) return;
        var important = text.Length > 0 && text[0] == PlanStore.ImportantMark;
        if (important) text = text.Substring(1);
        try
        {
            var notificationType = AccessTools.TypeByName("Keen.Game2.Client.UI.HUD.Notification.HudNotification");
            var create = notificationType?.GetMethod("CreateTextNotification", BindingFlags.Public | BindingFlags.Static);
            var show = inGameUi.GetType().GetMethods().FirstOrDefault(m => m.Name == "ShowNotification" && m.GetParameters().Length == 1);
            if (create == null || show == null) throw new MissingMethodException("notification API not found");

            var parameters = create.GetParameters();
            var args = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type == typeof(Keen.VRage.Library.Localization.LocKey)) args[i] = Patches.RawTexts.Make(text);
                else if (type == typeof(bool)) args[i] = true; // useTimer: the message goes away by itself
                else if (important && type.IsEnum && Enum.TryParse(type, type.Name.Contains("Priority") ? "High" : type.Name.Contains("Type") ? "Success" : "", out var chosen)) args[i] = chosen;
                else args[i] = type.IsValueType ? Activator.CreateInstance(type) : null; // default icon, priority, type
            }
            show.Invoke(inGameUi, new[] { create.Invoke(null, args) });
        }
        catch (Exception e)
        {
            unavailable = true;
            PluginLog.Warn("HUD notification failed, messages stay in the log only: " + (e.InnerException ?? e).Message);
        }
    }
}
