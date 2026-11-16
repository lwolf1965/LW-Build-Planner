using Keen.VRage.Library.Diagnostics;

namespace ClientPlugin.Game;

/// <summary>All plugin log lines start with [LwBuildPlanner] so they are easy to find in the game log.</summary>
internal static class PluginLog
{
    public static void Info(string message) => Log.Default.WriteLine($"[{Plugin.Name}] {message}");

    public static void Warn(string message) => Log.Default.WriteLine(LogSeverity.Warning, $"[{Plugin.Name}] {message}");

    public static void Error(string message) => Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] {message}");
}
