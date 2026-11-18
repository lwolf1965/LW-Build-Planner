using System;
using Avalonia.Threading;
using Keen.Game2.Client.UI.InGame;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;

namespace ClientPlugin.Game;

/// <summary>
/// The open key (default Ctrl+N, settings: Config.OpenKey) anywhere in a world (flying, walking, in a cockpit): opens
/// the planner window. Read through the game's own
/// input (IInputManager keyboard of the client session, as the HeadTracking plugin does), so it needs no Windows call.
/// While the terminal / G menu is open, GPick handles Ctrl+N instead (it also brings the selected block along).
/// Runs from the Session.Update postfix; the window is opened on the UI thread.
/// </summary>
internal static class Hotkeys
{
    private static DigitalInput key;
    private static int keyVk;
    private static bool keyLast, failed, alive;

    public static void OnSessionUpdate(Session session)
    {
        if (failed) return;
        try
        {
            if (session.TryGet<SessionInGameUISessionComponent>() == null) return; // only the player's own (client) session
            var keyboard = session.TryGet<IInputManager>()?.Keyboard;
            if (keyboard == null) return;
            if (!alive)
            {
                alive = true;
                PluginLog.Info($"{GPick.KeyName(Config.Current.OpenKey)} is read from the game's keyboard input (works anywhere in a world).");
            }

            var binding = Config.Current.OpenKey;
            if (!binding.IsBound) { keyLast = false; return; }
            // DigitalInput is a struct: made again only when the bound key changes (keyVk starts at 0, which is never bound).
            if (keyVk != binding.Vk) { key = new DigitalInput(binding.Vk, GenericDeviceClass.Keyboard); keyVk = binding.Vk; }
            var down = key.IsActive(keyboard);
            var pressed = down && !keyLast;
            keyLast = down;
            if (!pressed || KeyboardInputs.Control.IsActive(keyboard) != binding.Ctrl || KeyboardInputs.Shift.IsActive(keyboard) != binding.Shift
                || KeyboardInputs.Alt.IsActive(keyboard) != binding.Alt) return;
            if (GPick.TerminalOpen) return; // the G menu's own hook takes it there
            Dispatcher.UIThread.Post(Screens.PlannerScreen.Open);
        }
        catch (Exception e)
        {
            failed = true;
            PluginLog.Error("Open key outside the G menu switched off: " + e);
        }
    }
}
