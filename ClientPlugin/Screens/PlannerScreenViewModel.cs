using Keen.VRage.UI.Screens;

namespace ClientPlugin.Screens;

/// <summary>View model of the planner window (same pattern as the plugin settings screens: a full-screen ScreenView).</summary>
internal class PlannerScreenViewModel : ScreenViewModel
{
    public PlannerScreenViewModel()
    {
        KeepsOtherScreensVisible = false;
        AllowsInputBelowUI = false;
        AllowsInputFromLowerScreens = false;
        InitializeInputContext();
    }
}
