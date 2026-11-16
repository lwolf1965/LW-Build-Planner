using System;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>Shared order helpers. The ordering itself lives in OrderBook.</summary>
internal static class OrderJob
{
    /// <summary>True = log only, nothing is queued.</summary>
    public static bool DryRun = false;

    /// <summary>The item's display name from the game (ItemDefinition.DisplayName), else the last part of its content path.</summary>
    public static string NameOf(ItemDefinition item)
    {
        if (item == null) return "?";
        try
        {
            var name = GPick.Text(Reflect.Get(Reflect.Member(item.GetType(), "DisplayName"), item));
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception) { /* fall back to the path */ }
        var text = item.ToString() ?? "?";
        var cut = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
        return cut >= 0 ? text.Substring(cut + 1) : text;
    }
}
