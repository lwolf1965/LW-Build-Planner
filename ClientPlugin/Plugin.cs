using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core.Plugins;

namespace ClientPlugin;

public class Plugin : IPlugin
{
    public const string Name = "LwBuildPlanner";
    /// <summary>Plugin version, the only place to change it (AssemblyInfo.cs uses it). STAGE.FEATURE.FIX.BUILD, 0.1 = game alpha.</summary>
    public const string Version = "0.1.0.1";
    public static Plugin Instance;

    // Injected by Pulsar (same mechanism as LW Cargo Manager).
#pragma warning disable CS0649
    private static Func<string, string, string> GetConfigPath;
#pragma warning restore CS0649
    // Pulsar's data folder for this plugin, asked for only when something uses it: asking creates the folder, and this
    // version stores nothing there (hub review of PR #41, 2026-11-19). The settings of the next version use it.
    private string dataDir;
    public string DataDir => dataDir ??= GetConfigPath(Name, null);

    public Plugin()
    {
        Instance = this;
        Game.PluginLog.Info($"Loaded {Name} {Version}");

        // Patch each class separately: one failed hook must not stop the others.
        var harmony = new Harmony(Name);
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!type.GetCustomAttributes<HarmonyPatch>().Any()) continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                Game.PluginLog.Info($"Patched {type.Name}");
            }
            catch (Exception e)
            {
                Game.PluginLog.Error($"Patch {type.Name} failed: {e}");
            }
        }
    }

    /// <summary>Invoked by Pulsar when the player clicks the plugin's config button: opens the planner window.</summary>
    public void OpenConfigDialog() => Screens.PlannerScreen.Open();
}
