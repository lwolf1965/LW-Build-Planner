using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;

namespace ClientPlugin.Patches;

// The game shows HUD texts through LocKey, a key into its localization tables. A plugin's own text is not a key there,
// so the game logs "LOCALIZATION WARNING: Could not find key ..." for every message (and, per the SE2Online plugin,
// turns the first such warning into a crash report at exit). The planner registers its texts (already translated by
// Policy/Tr) here, and these two prefixes return them as they are instead of looking them up. Every other text of the
// game goes through untouched; a text the game itself knows is never replaced.
// Same approach as the SE2Online plugin (yellow5111/SE2Online, OnlineUi.InstallRawTexts) in PluginHub-SE2.

/// <summary>The planner's texts shown through the game's localized UI.</summary>
internal static class RawTexts
{
    private static readonly ConcurrentDictionary<StringId, bool> Known = new();

    /// <summary>A LocKey that shows the text as it is, without a localization warning.</summary>
    public static LocKey Make(string text)
    {
        if (Known.Count > 2000) Known.Clear(); // messages carry numbers; old ones are not shown again
        Known.TryAdd(text, true);
        return LocKey.FromString(text);
    }

    public static bool IsOurs(StringId id) => !Known.IsEmpty && Known.ContainsKey(id);
}

/// <summary>Looking up one of our texts returns the text itself (escaped, so the game's {filter} parser leaves it alone).</summary>
[HarmonyPatch(typeof(LocalizationPackage), nameof(LocalizationPackage.GetText), new[] { typeof(StringId) })]
internal static class RawTextLookupPatch
{
    private static bool Prefix(LocalizationPackage __instance, StringId __0, ref string __result)
    {
        if (!RawTexts.IsOurs(__0) || __instance.HasText(__0)) return true;
        __result = TextManager.EscapeInputString(__0.ToString());
        return false;
    }
}

/// <summary>Turning one of our LocKeys into a string returns the text, without the warning the game logs for unknown keys.</summary>
[HarmonyPatch(typeof(LocKey), nameof(LocKey.ToString), new Type[0])]
internal static class RawTextToStringPatch
{
    private static bool Prefix(ref LocKey __instance, ref string __result)
    {
        if (!RawTexts.IsOurs(__instance.TextId) || Singleton<TextManager>.Instance.HasText(__instance)) return true;
        __result = __instance.TextId.ToString();
        return false;
    }
}
