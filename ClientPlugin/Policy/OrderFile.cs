using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>Blocks of one build list line in a running order (OrderBook's OrderGroup), as text ids.</summary>
public sealed class SavedGroup
{
    /// <summary>The list line's key, as PlanLine.Key writes it.</summary>
    public string Key;
    /// <summary>The line belongs to the blueprint project.</summary>
    public bool Project;
    public int Count;
    /// <summary>Blocks of the group already done (all parts handed out).</summary>
    public int Done;
    /// <summary>Parts per block.</summary>
    public Dictionary<string, long> Parts = new();
}

/// <summary>One ordered part: the ledger's numbers for it.</summary>
public sealed class SavedPart
{
    public string Item;
    public long Ordered;
    public long Done;
    /// <summary>Not handed to a crafter yet.</summary>
    public long Waiting;
    /// <summary>In crafters' queues (the game saves the queues; the crafters are found again after the load).</summary>
    public long Queued;
}

/// <summary>The planner's running orders, as saved with the world.</summary>
public sealed class SavedOrders
{
    /// <summary>"All" or "Missing": the button that started the order.</summary>
    public string Mode;
    /// <summary>Seconds the orders had been running.</summary>
    public double Elapsed;
    public List<SavedGroup> Groups = new();
    /// <summary>Parts handed out for the groups so far.</summary>
    public Dictionary<string, long> HandedOut = new();
    /// <summary>In the order they were ordered (oldest first).</summary>
    public List<SavedPart> Parts = new();
}

/// <summary>
/// The orders file, one per world next to the build list file (Lone 2026-11-20: after a load the order goes on by
/// itself). Rows separated by tabs: "mode", "group", "handed", "part". Unreadable rows are skipped; a file with an
/// unknown header is ignored as a whole.
/// </summary>
public static class OrderFile
{
    public const string Header = "LW Build Planner orders 1";

    public static string Write(SavedOrders orders)
    {
        var text = new StringBuilder().Append(Header).Append('\n');
        text.Append("mode\t").Append(Clean(orders.Mode)).Append('\t').Append(orders.Elapsed.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var group in orders.Groups)
            text.Append("group\t").Append(group.Project ? "p" : "-").Append('\t').Append(Clean(group.Key)).Append('\t')
                .Append(N(group.Count)).Append('\t').Append(N(group.Done)).Append('\t')
                .Append(string.Join(";", group.Parts.Select(p => Clean(p.Key) + "=" + N(p.Value)))).Append('\n');
        foreach (var handed in orders.HandedOut)
            text.Append("handed\t").Append(Clean(handed.Key)).Append('\t').Append(N(handed.Value)).Append('\n');
        foreach (var part in orders.Parts)
            text.Append("part\t").Append(Clean(part.Item)).Append('\t').Append(N(part.Ordered)).Append('\t').Append(N(part.Done))
                .Append('\t').Append(N(part.Waiting)).Append('\t').Append(N(part.Queued)).Append('\n');
        return text.ToString();
    }

    /// <summary>The orders, or null (no file, empty or unknown file).</summary>
    public static SavedOrders Read(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var rows = text.Replace("\r\n", "\n").Split('\n');
        if (rows[0].Trim() != Header) return null;
        var orders = new SavedOrders();
        foreach (var row in rows.Skip(1))
        {
            var cells = row.Split('\t');
            switch (cells[0])
            {
                case "mode" when cells.Length >= 2:
                    orders.Mode = cells[1].Trim();
                    if (cells.Length >= 3) double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out orders.Elapsed);
                    break;
                case "group" when cells.Length >= 6 && Int(cells[3], out var count) && Int(cells[4], out var done) && count > 0:
                    var group = new SavedGroup { Project = cells[1] == "p", Key = cells[2].Trim(), Count = count, Done = Math.Min(done, count) };
                    foreach (var pair in cells[5].Split(';'))
                    {
                        var kv = pair.Split('=');
                        if (kv.Length == 2 && Long(kv[1], out var n) && n > 0 && kv[0].Trim().Length > 0) group.Parts[kv[0].Trim()] = n;
                    }
                    if (group.Key.Length > 0 && group.Parts.Count > 0) orders.Groups.Add(group);
                    break;
                case "handed" when cells.Length >= 3 && Long(cells[2], out var handed) && handed > 0:
                    orders.HandedOut[cells[1].Trim()] = handed;
                    break;
                case "part" when cells.Length >= 6 && Long(cells[2], out var ordered) && Long(cells[3], out var partDone)
                                 && Long(cells[4], out var waiting) && Long(cells[5], out var queued):
                    orders.Parts.Add(new SavedPart
                    {
                        Item = cells[1].Trim(), Ordered = ordered, Done = partDone, Waiting = Math.Max(0, waiting), Queued = Math.Max(0, queued),
                    });
                    break;
            }
        }
        return orders.Groups.Count == 0 && orders.Parts.All(p => p.Waiting + p.Queued <= 0) ? null : orders;
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool Int(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static bool Long(string text, out long value) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static string Clean(string value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace(';', ' ').Replace('=', ' ');
}
