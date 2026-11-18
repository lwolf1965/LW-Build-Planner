using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>
/// The planner's own texts in the 13 languages of the game (window, HUD messages, alerts). The game's language is
/// set with Use (from the game's culture, for example "ru-RU" or "cz-CZ"); unknown languages fall back to English.
/// Block, part and crafter names are not here: they come from the game, already in the player's language.
/// The texts themselves are in TrTable.cs.
/// </summary>
public static partial class Tr
{
    /// <summary>Language codes in the order of the rows in TrTable.cs. en first: the fallback.</summary>
    public static readonly string[] Languages = { "en", "cs", "de", "es", "fr", "it", "pl", "pt", "ru", "sk", "tr", "uk", "zh" };

    private static volatile string language = "en";

    /// <summary>The language in use (a code from Languages).</summary>
    public static string Language => language;

    /// <summary>Switches to the game's language; returns the code now in use.</summary>
    public static string Use(string gameCulture)
    {
        var code = Normalize(gameCulture);
        language = code;
        return code;
    }

    /// <summary>"ru-RU" to "ru", the game's "cz-CZ" to "cs", "pt-BR" to "pt"; anything unknown to "en".</summary>
    public static string Normalize(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en";
        var code = culture.Trim().ToLowerInvariant();
        var cut = code.IndexOfAny(new[] { '-', '_' });
        if (cut > 0) code = code.Substring(0, cut);
        if (code == "cz") code = "cs";
        if (code == "ua") code = "uk";
        return Array.IndexOf(Languages, code) >= 0 ? code : "en";
    }

    /// <summary>The text in the language in use, with {0}, {1} ... filled in.</summary>
    public static string T(string key, params object[] args) => In(language, key, args);

    /// <summary>The text in a given language (code from Languages); English when that language has none; the key when unknown.</summary>
    public static string In(string code, string key, params object[] args)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        var index = Array.IndexOf(Languages, code);
        var text = index >= 0 && index < row.Length && !string.IsNullOrEmpty(row[index]) ? row[index] : row[0];
        if (args == null || args.Length == 0) return text;
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>
    /// A settings dialog text (label, description, section, title) in the language in use. The settings attributes in
    /// Config.cs keep their English text; its translation is the row "set:" + that text, so a text with no row stays English.
    /// </summary>
    public static string Setting(string english)
        => string.IsNullOrEmpty(english) || !Table.ContainsKey("set:" + english) ? english : T("set:" + english);

    /// <summary>All text keys (for the tests).</summary>
    public static IEnumerable<string> Keys => Table.Keys;

    /// <summary>The row of one key, one text per language (for the tests).</summary>
    public static string[] Row(string key) => Table.TryGetValue(key, out var row) ? row : null;
}
