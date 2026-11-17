using System;
using System.Globalization;
using System.Reflection;
using ClientPlugin.Policy;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;

namespace ClientPlugin.Game;

/// <summary>
/// Picks the planner's language from the game's. Game API (VRage.Library, read from the DLL):
///   Singleton&lt;TextManager&gt;.Instance._textSource (non-public; also the non-public property TextSource) is the
///   ITextSource (LocalizationPackage) the game reads its texts from; ITextSource.CurrentCulture is a StringId such as
///   "ru-RU" or "cz-CZ". LocalizationManager.CurrentCulture reads the same value.
/// The player can switch the language in a running game, so this is called often and is cheap after the first time:
/// when a world loads, when the window opens, on every + in the G menu and on every order tick.
/// When the value cannot be read the system's UI language is used.
/// </summary>
internal static class GameLanguage
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo SourceProperty = typeof(TextManager).GetProperty("TextSource", Members);
    private static readonly FieldInfo SourceField = typeof(TextManager).GetField("_textSource", Members);
    private static bool told;
    private static string last;

    public static void Refresh()
    {
        string culture = null;
        try
        {
            var manager = Singleton<TextManager>.Instance;
            var source = manager == null ? null : SourceProperty?.GetValue(manager) ?? SourceField?.GetValue(manager);
            if (source is ITextSource texts) culture = texts.CurrentCulture.ToString();
            if (!told)
            {
                told = true;
                PluginLog.Info(culture != null
                    ? "Language: read from the game's text source (TextManager._textSource.CurrentCulture)."
                    : "Language: the game's text source was not found, the system language is used.");
            }
        }
        catch (Exception e)
        {
            if (!told)
            {
                told = true;
                PluginLog.Warn("Language: reading the game's culture failed, the system language is used: " + e.Message);
            }
        }
        if (string.IsNullOrWhiteSpace(culture)) culture = CultureInfo.CurrentUICulture.Name;

        var code = Tr.Use(culture);
        if (code != last)
        {
            last = code;
            PluginLog.Info($"Language: game culture '{culture}', planner texts in '{code}'.");
        }
    }
}
