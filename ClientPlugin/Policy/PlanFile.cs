using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One saved build list line: definition ids as text, so the file needs no game types.</summary>
public sealed class PlanLine
{
    /// <summary>Guid of the picked definition (the G menu's block, often an EntityCompositeDefinition).</summary>
    public string Key;
    /// <summary>Guid of the CubeBlockDefinition the cost comes from.</summary>
    public string Block;
    public int Count;
    /// <summary>Name as shown when it was picked; used only when the game no longer knows the block.</summary>
    public string Name;
    /// <summary>The line belongs to the blueprint project (no line limit), not to the hand-picked blocks.</summary>
    public bool Project;
}

/// <summary>
/// The build list file, one per world: a header line, then one line per block: key, block, count, name and (version 2)
/// "p" for a line of the blueprint project, separated by tabs. Version 2 may have one "#project" row with the
/// blueprint's name. Version 1 files (no project) are still read. Unreadable lines are skipped, so a damaged file loses
/// only those lines; a file with an unknown header is ignored as a whole.
/// </summary>
public static class PlanFile
{
    public const string Header = "LW Build Planner list 2";
    public const string HeaderV1 = "LW Build Planner list 1";
    private const string ProjectRow = "#project";
    private const string SavedRow = "#saved";

    /// <param name="started">The blueprint was ordered already: its next rounds are made from ore only (BlockLine.NoStock).</param>
    /// <param name="savedAt">The world's game time at the save (ticks), 0 = unknown: a load of an older save is told by it.</param>
    public static string Write(IEnumerable<PlanLine> lines, string project = null, bool started = false, long savedAt = 0)
    {
        var text = new StringBuilder().Append(Header).Append('\n');
        if (savedAt > 0) text.Append(SavedRow).Append('\t').Append(savedAt.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(project))
            text.Append(ProjectRow).Append('\t').Append(Clean(project)).Append(started ? "\tstarted" : "").Append('\n');
        foreach (var line in lines.Where(l => l.Count > 0 && !string.IsNullOrWhiteSpace(l.Block)))
            text.Append(Clean(line.Key ?? line.Block)).Append('\t').Append(Clean(line.Block)).Append('\t')
                .Append(line.Count.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(line.Name))
                .Append(line.Project ? "\tp" : "").Append('\n');
        return text.ToString();
    }

    public static List<PlanLine> Read(string text) => Read(text, out _);

    public static List<PlanLine> Read(string text, out string project) => Read(text, out project, out _);

    public static List<PlanLine> Read(string text, out string project, out bool started) => Read(text, out project, out started, out _);

    /// <summary>
    /// The lines, the blueprint project's name (null when the list has no project), whether it was ordered already and
    /// the world's game time when the file was written (0 = unknown).
    /// </summary>
    public static List<PlanLine> Read(string text, out string project, out bool started, out long savedAt)
    {
        project = null;
        started = false;
        savedAt = 0;
        var result = new List<PlanLine>();
        if (string.IsNullOrEmpty(text)) return result;
        var rows = text.Replace("\r\n", "\n").Split('\n');
        var header = rows[0].Trim();
        if (header != Header && header != HeaderV1) return result;
        foreach (var row in rows.Skip(1))
        {
            var cells = row.Split('\t');
            if (cells[0] == SavedRow)
            {
                if (cells.Length > 1) long.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out savedAt);
                continue;
            }
            if (cells[0] == ProjectRow)
            {
                if (cells.Length > 1 && !string.IsNullOrWhiteSpace(cells[1])) project = cells[1].Trim();
                started = cells.Length > 2 && cells[2].Trim() == "started";
                continue;
            }
            if (cells.Length < 3) continue;
            if (!int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0) continue;
            if (string.IsNullOrWhiteSpace(cells[1])) continue;
            result.Add(new PlanLine
            {
                Key = cells[0].Trim(), Block = cells[1].Trim(), Count = count, Name = cells.Length > 3 ? cells[3] : "",
                Project = cells.Length > 4 && cells[4].Trim() == "p",
            });
        }
        if (project == null && result.Any(l => l.Project)) project = "?"; // project lines without their name row
        return result;
    }

    /// <summary>A world id as a safe file name: letters, digits, '-' and '_' only; anything else is replaced, at most 80 characters.</summary>
    public static string FileName(string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId)) return null;
        var name = new string(worldId.Trim().Select(c => char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' ? c : '_').ToArray());
        if (name.Trim('_', '-', '0').Length == 0) return null; // empty or an all-zero id: the world has no id (not saved yet)
        return (name.Length > 80 ? name.Substring(0, 80) : name) + ".txt";
    }

    private static string Clean(string value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
