using System;

namespace ClientPlugin.Policy;

/// <summary>
/// The player's settings as the planner's logic sees them (pure values, no game or UI types, so the tests can use them).
/// Filled from the settings dialog (Config) at start and after every change; the defaults are the planner's behaviour
/// without a config file. Replaced as a whole (never changed in place), so a reader on another thread always sees one
/// consistent set.
/// </summary>
public sealed class PlannerSettings
{
    // Steps of +, - and the window's [-] [+] buttons. Alt beats Ctrl beats Shift.
    public int Step = 1, StepShift = 5, StepCtrl = 10, StepAlt = 25;
    public int ArmorStep = 1, ArmorStepShift = 10, ArmorStepCtrl = 100, ArmorStepAlt = 500;

    // Most blocks per hand-picked list line.
    public int MaxPerLine = 50, ArmorMaxPerLine = 1000;

    // Orders
    /// <summary>Crafts per batch that a free crafter gets at once (Dispatch).</summary>
    public int BatchCrafts = 10;
    /// <summary>Cargo space warning above this share of the free capacity, in percent (PlanAlerts).</summary>
    public int CargoWarnPercent = 80;
    /// <summary>A part with no arrivals and no crafter working on it for this long is "no progress".</summary>
    public int NoProgressSeconds = 60;
    /// <summary>When all orders are done, keep the ordered blocks in the list (to order the same build again).</summary>
    public bool KeepListWhenDone;
    /// <summary>Empty list and Stop orders need a second click.</summary>
    public bool ConfirmClear;

    public static volatile PlannerSettings Current = new();

    /// <summary>A copy with every number kept in a sane range (a hand-edited config file cannot break the planner).</summary>
    public PlannerSettings Checked()
    {
        static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        return new PlannerSettings
        {
            Step = Clamp(Step, 1, 10000), StepShift = Clamp(StepShift, 1, 10000), StepCtrl = Clamp(StepCtrl, 1, 10000), StepAlt = Clamp(StepAlt, 1, 10000),
            ArmorStep = Clamp(ArmorStep, 1, 10000), ArmorStepShift = Clamp(ArmorStepShift, 1, 10000),
            ArmorStepCtrl = Clamp(ArmorStepCtrl, 1, 10000), ArmorStepAlt = Clamp(ArmorStepAlt, 1, 10000),
            MaxPerLine = Clamp(MaxPerLine, 1, 100000), ArmorMaxPerLine = Clamp(ArmorMaxPerLine, 1, 100000),
            BatchCrafts = Clamp(BatchCrafts, 1, 1000), CargoWarnPercent = Clamp(CargoWarnPercent, 10, 100),
            NoProgressSeconds = Clamp(NoProgressSeconds, 10, 3600),
            KeepListWhenDone = KeepListWhenDone, ConfirmClear = ConfirmClear,
        };
    }
}
