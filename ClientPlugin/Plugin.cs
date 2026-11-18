using System;
using System.Linq;
using System.Reflection;
using ClientPlugin.Settings;
using ClientPlugin.Tools;
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
    public string DataDir { get; private set; } = GetConfigPath(Name, null);

    public Plugin()
    {
        Instance = this;
        Game.PluginLog.Info($"Loaded {Name} {Version}");
        try
        {
            Config.Current.Apply(); // settings (Config.xml in DataDir) for the planner's logic
            Game.PluginLog.Info($"Settings loaded: add key {Game.GPick.KeyName(Config.Current.AddKey)}, open key {Game.GPick.KeyName(Config.Current.OpenKey)}.");
        }
        catch (Exception e)
        {
            Game.PluginLog.Error("Settings could not be loaded, defaults are used: " + e);
        }

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

    /// <summary>
    /// Invoked by Pulsar when the player clicks the plugin's config button (and by the Settings button of the planner
    /// window): opens the settings dialog. Saved to Config.xml when the dialog closes.
    /// </summary>
    public void OpenConfigDialog()
    {
        try
        {
            var sharedUi = GameAccess.GetSharedUI();
            if (sharedUi == null)
            {
                Game.PluginLog.Warn("Settings: the game UI is not available.");
                return;
            }
            try { Game.GameLanguage.Refresh(); } // the dialog in the game's current language (also from the main menu)
            catch (Exception e) { Game.PluginLog.Warn("Settings: language not read: " + e.Message); }
            var generator = new SettingsGenerator();
            var viewModel = new SettingsScreenViewModel(generator.Title, panel => generator.PopulateContent(panel), () =>
            {
                try
                {
                    ConfigStorage.Save(Config.Current);
                    var c = Config.Current;
                    Game.PluginLog.Info($"Settings saved: add key {Game.GPick.KeyName(c.AddKey)}, open key {Game.GPick.KeyName(c.OpenKey)}, "
                                        + $"keep list {c.KeepListWhenDone}, confirm {c.ConfirmClear}, batch {c.BatchCrafts}, cargo warning {c.CargoWarnPercent} %, "
                                        + $"no progress {c.NoProgressSeconds} s, steps {c.StepShift}/{c.StepCtrl}/{c.StepAlt} max {c.MaxPerLine}, "
                                        + $"armor {c.ArmorStepShift}/{c.ArmorStepCtrl}/{c.ArmorStepAlt} max {c.ArmorMaxPerLine}.");
                }
                catch (Exception e)
                {
                    Game.PluginLog.Warn("Settings could not be saved: " + e.Message);
                }
            });
            sharedUi.CreateScreen<SettingsScreen>(viewModel, showCursor: true);
        }
        catch (Exception e)
        {
            Game.PluginLog.Error("Settings dialog failed: " + e);
        }
    }
}
