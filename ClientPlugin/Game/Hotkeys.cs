using System;
using Avalonia.Threading;
using Keen.Game2.Client.UI.InGame;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;

namespace ClientPlugin.Game;

/// <summary>
/// Ctrl+N anywhere in a world (flying, walking, in a cockpit): opens the planner window. Read through the game's own
/// input (IInputManager keyboard of the client session, as the HeadTracking plugin does), so it needs no Windows call.
/// While the terminal / G menu is open, GPick handles Ctrl+N instead (it also brings the selected block along).
/// Runs from the Session.Update postfix; the window is opened on the UI thread.
/// </summary>
internal static class Hotkeys
{
    private const int VkN = 0x4E;
    private static readonly DigitalInput KeyN = new(VkN, GenericDeviceClass.Keyboard);
    private static bool nLast, failed, alive;

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
                PluginLog.Info("Ctrl+N is read from the game's keyboard input (works anywhere in a world).");
            }

            var down = KeyN.IsActive(keyboard);
            var pressed = down && !nLast;
            nLast = down;
            if (!pressed || !KeyboardInputs.Control.IsActive(keyboard) || KeyboardInputs.Shift.IsActive(keyboard) || KeyboardInputs.Alt.IsActive(keyboard)) return;
            if (GPick.TerminalOpen) return; // the G menu's own hook takes it there
            Dispatcher.UIThread.Post(Screens.PlannerScreen.Open);
        }
        catch (Exception e)
        {
            failed = true;
            PluginLog.Error("Ctrl+N outside the G menu switched off: " + e);
        }
    }
}
