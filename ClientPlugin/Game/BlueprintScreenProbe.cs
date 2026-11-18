// TEMP PROBE (blueprint branch only, never committed: *Probe.cs is git-ignored). Delete when the blueprint screen is settled.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HarmonyLib;
using Keen.VRage.Core.Game.Systems;

namespace ClientPlugin.Game;

/// <summary>
/// Blueprint screen probe: where could the planner write "can it be built" into the game's own blueprint screens?
/// Captures the game's blueprint views when they are created (TerminalBlueprintsScreen = the Blueprints tab,
/// DetailsScreen = the detail screen of a blueprint, DetailActionsControl = its action buttons), then on Ctrl+Shift+N
/// logs their visual trees (control type, name, view model type, short text), lines start with "[LwBuildPlanner] BS".
/// Reads only.
/// </summary>
[HarmonyPatch]
internal static class BlueprintScreenProbe
{
    private const int VkN = 0x4E, VkShift = 0x10, VkControl = 0x11;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static readonly List<WeakReference<Control>> Views = new();
    private static bool nLast;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     "Keen.Game2.Client.UI.TerminalScreen.Blueprint.TerminalBlueprintsScreen",
                     "Keen.Game2.Client.UI.Shared.UGC.Detail.DetailsScreen",
                     "Keen.Game2.Client.UI.Shared.UGC.Detail.DetailActions.DetailActionsControl",
                 })
        {
            var type = AccessTools.TypeByName(name);
            if (type == null) { PluginLog.Warn("BS type not found: " + name); continue; }
            foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                yield return ctor;
        }
    }

    private static void Postfix(object __instance)
    {
        if (__instance is not Control control) return;
        lock (Views)
        {
            Views.RemoveAll(w => !w.TryGetTarget(out _));
            Views.Add(new WeakReference<Control>(control));
        }
        Log($"created {control.GetType().Name}");
    }

    private static void Log(string text) => PluginLog.Info("BS " + text);

    /// <summary>Ctrl+Shift+N in any screen of a world: dump the captured views on the UI thread.</summary>
    [HarmonyPatch(typeof(Session), nameof(Session.Update), typeof(bool))]
    private static class KeyPatch
    {
        private static void Postfix()
        {
            try
            {
                var down = (GetAsyncKeyState(VkN) & 0x8000) != 0;
                var pressed = down && !nLast;
                nLast = down;
                if (!pressed || (GetAsyncKeyState(VkControl) & 0x8000) == 0 || (GetAsyncKeyState(VkShift) & 0x8000) == 0) return;
                Dispatcher.UIThread.Post(Dump);
            }
            catch (Exception e) { Log("key failed: " + e.Message); }
        }
    }

    private static void Dump()
    {
        try
        {
            List<Control> views;
            lock (Views) views = Views.Select(w => w.TryGetTarget(out var c) ? c : null).Where(c => c != null).ToList();
            Log($"---- Ctrl+Shift+N: {views.Count} captured view(s)");
            foreach (var view in views)
            {
                var attached = view.GetVisualRoot() != null;
                Log($"== {view.GetType().FullName}, attached {attached}, visible {view.IsVisible}, size {view.Bounds.Width:0}x{view.Bounds.Height:0}, data {view.DataContext?.GetType().Name}");
                if (!attached) continue;
                var lines = 0;
                Walk(view, 0, ref lines);
                if (view.DataContext != null) Members(view.DataContext);
            }
        }
        catch (Exception e) { Log("dump failed: " + e); }
    }

    private static void Walk(Visual visual, int depth, ref int lines)
    {
        if (lines > 500 || depth > 25) return;
        lines++;
        var control = visual as Control;
        var text = visual switch
        {
            TextBlock t => " text='" + Short(t.Text) + "'",
            ContentControl c when c.Content is string s => " content='" + Short(s) + "'",
            _ => "",
        };
        var panel = visual is Panel p ? $" panel children {p.Children.Count}" : "";
        Log($"{new string(' ', depth)}{visual.GetType().Name}{(string.IsNullOrEmpty(control?.Name) ? "" : " #" + control.Name)}"
            + $" [{visual.Bounds.Width:0}x{visual.Bounds.Height:0}]{(control?.IsVisible == false ? " hidden" : "")}"
            + $"{(control?.DataContext != null && control.DataContext != (control.Parent as Control)?.DataContext ? " data " + control.DataContext.GetType().Name : "")}{panel}{text}");
        foreach (var child in visual.GetVisualChildren()) Walk(child, depth + 1, ref lines);
    }

    private static void Members(object viewModel)
    {
        var parts = new List<string>();
        for (var t = viewModel.GetType(); t != null && t != typeof(object); t = t.BaseType)
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object value;
                try { value = p.GetValue(viewModel); }
                catch (Exception) { continue; }
                parts.Add($"{p.Name}={(value == null ? "null" : value is string s ? "'" + Short(s) + "'" : value.GetType().Name)}");
            }
        Log($"view model {viewModel.GetType().Name}: {string.Join("; ", parts.Take(80))}");
    }

    private static string Short(string text)
    {
        if (text == null) return "";
        text = text.Replace('\n', ' ');
        return text.Length > 50 ? text.Substring(0, 50) + "..." : text;
    }
}
