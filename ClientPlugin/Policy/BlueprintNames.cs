using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One kind of block in a blueprint: the block's name, its definition path (for the size) and how many.</summary>
public sealed class BlueprintKind<TKey>
{
    public TKey Key;
    public string Name;
    public string Path;
    public int Count;
}

/// <summary>Names for the lines of a blueprint project.</summary>
public static class BlueprintNames
{
    /// <summary>
    /// The block size from a definition path such as "Vanilla\Blocks\Thrusters\Hydrogen\250\HydrogenThruster250_ServerComposition.def":
    /// the last folder made only of digits, in centimetres ("250" = 2.5 m). Null when the path has none.
    /// </summary>
    public static string SizeOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split('\\', '/');
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            var part = parts[i];
            if (part.Length == 0 || !part.All(char.IsDigit) || !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cm) || cm <= 0) continue;
            return (cm / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " m";
        }
        return null;
    }

    /// <summary>
    /// Blocks of the same name (one block in two sizes) get the size after the name, so the lines can be told apart;
    /// lines are sorted by name. Kinds are counted once per key (the counts of the same key are added up).
    /// </summary>
    public static List<BlueprintKind<TKey>> Lines<TKey>(IEnumerable<BlueprintKind<TKey>> kinds)
    {
        var merged = new List<BlueprintKind<TKey>>();
        var byKey = new Dictionary<TKey, BlueprintKind<TKey>>();
        foreach (var kind in kinds.Where(k => k != null && k.Key != null && k.Count > 0))
        {
            if (byKey.TryGetValue(kind.Key, out var known)) { known.Count += kind.Count; continue; }
            var copy = new BlueprintKind<TKey> { Key = kind.Key, Name = string.IsNullOrWhiteSpace(kind.Name) ? "?" : kind.Name, Path = kind.Path, Count = kind.Count };
            byKey[kind.Key] = copy;
            merged.Add(copy);
        }
        foreach (var group in merged.GroupBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var kind in group)
            {
                var size = SizeOf(kind.Path);
                if (size != null) kind.Name += " " + size;
            }
        return merged.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
