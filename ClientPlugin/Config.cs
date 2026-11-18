using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using ClientPlugin.Policy;
using ClientPlugin.Settings.Elements;
using ClientPlugin.Settings.Tools;

namespace ClientPlugin;

/// <summary>
/// The planner's settings, shown in the settings dialog (Pulsar's config button, or Settings in the planner window) and
/// stored in Config.xml in the plugin's data folder (ConfigStorage). The rows of the dialog are generated from the
/// attributes (settings system of the client plugin template). Every change goes to PlannerSettings.Current at once,
/// which is what the planner's logic reads.
/// </summary>
public class Config : INotifyPropertyChanged
{
    #region Options

    private bool keepListWhenDone;
    private bool confirmClear;
    private int batchCrafts = 10;
    private int cargoWarnPercent = 80;
    private int noProgressSeconds = 60;
    private Binding addKey = new Binding(0xBB);              // + (numpad + works too while this is +)
    private Binding openKey = new Binding(0x4E, ctrl: true);  // Ctrl+N
    private int stepShift = 5, stepCtrl = 10, stepAlt = 25, maxPerLine = 50;
    private int armorStepShift = 10, armorStepCtrl = 100, armorStepAlt = 500, armorMaxPerLine = 1000;

    #endregion

    #region User interface

    [XmlIgnore]
    public readonly string Title = "LW Build Planner settings";

    [Separator("Orders")]
    [Checkbox(label: "Keep list when done", description: "Off: when all orders are done, the ordered blocks leave the build list. On: they stay, so the same build can be ordered again.")]
    public bool KeepListWhenDone
    {
        get => keepListWhenDone;
        set => SetField(ref keepListWhenDone, value);
    }

    [Checkbox(label: "Confirm clearing", description: "Empty list and Stop orders need a second click.")]
    public bool ConfirmClear
    {
        get => confirmClear;
        set => SetField(ref confirmClear, value);
    }

    [Slider(1, 50, 1, SliderAttribute.SliderType.Integer, label: "Batch size", description: "Crafts a free crafter gets at once. Smaller batches spread a part over more crafters; bigger ones mean fewer pauses. Default 10.")]
    public int BatchCrafts
    {
        get => batchCrafts;
        set => SetField(ref batchCrafts, value);
    }

    [Slider(50, 100, 5, SliderAttribute.SliderType.Integer, label: "Cargo warning %", description: "Warn when the new parts need more than this share of the free cargo space. Default 80.")]
    public int CargoWarnPercent
    {
        get => cargoWarnPercent;
        set => SetField(ref cargoWarnPercent, value);
    }

    [Slider(30, 300, 10, SliderAttribute.SliderType.Integer, label: "No progress after (s)", description: "A part shows 'no progress' when nothing arrived and no crafter worked on it for this long. Default 60.")]
    public int NoProgressSeconds
    {
        get => noProgressSeconds;
        set => SetField(ref noProgressSeconds, value);
    }

    [Separator("Keys")]
    [Keybind(label: "Add block", description: "In the G menu: adds the selected block. Hold Shift, Ctrl or Alt with it for the bigger steps below (modifiers set here are ignored). Default +. Right click clears it.")]
    public Binding AddKey
    {
        get => addKey;
        set => SetField(ref addKey, value);
    }

    [Keybind(label: "Open planner", description: "Opens the planner window anywhere in a world. Default Ctrl+N. Right click clears it.")]
    public Binding OpenKey
    {
        get => openKey;
        set => SetField(ref openKey, value);
    }

    [Separator("Steps: blocks")]
    [Slider(1, 50, 1, SliderAttribute.SliderType.Integer, label: "Shift step", description: "Blocks added or removed with Shift. Default 5.")]
    public int StepShift
    {
        get => stepShift;
        set => SetField(ref stepShift, value);
    }

    [Slider(1, 100, 1, SliderAttribute.SliderType.Integer, label: "Ctrl step", description: "Blocks added or removed with Ctrl. Default 10.")]
    public int StepCtrl
    {
        get => stepCtrl;
        set => SetField(ref stepCtrl, value);
    }

    [Slider(1, 500, 1, SliderAttribute.SliderType.Integer, label: "Alt step", description: "Blocks added or removed with Alt. Default 25.")]
    public int StepAlt
    {
        get => stepAlt;
        set => SetField(ref stepAlt, value);
    }

    [Slider(10, 1000, 10, SliderAttribute.SliderType.Integer, label: "Most per line", description: "Most blocks per build list line. Default 50.")]
    public int MaxPerLine
    {
        get => maxPerLine;
        set => SetField(ref maxPerLine, value);
    }

    [Separator("Steps: armor")]
    [Slider(1, 100, 1, SliderAttribute.SliderType.Integer, label: "Shift step", description: "Armor blocks added or removed with Shift. Default 10.")]
    public int ArmorStepShift
    {
        get => armorStepShift;
        set => SetField(ref armorStepShift, value);
    }

    [Slider(10, 1000, 10, SliderAttribute.SliderType.Integer, label: "Ctrl step", description: "Armor blocks added or removed with Ctrl. Default 100.")]
    public int ArmorStepCtrl
    {
        get => armorStepCtrl;
        set => SetField(ref armorStepCtrl, value);
    }

    [Slider(50, 5000, 50, SliderAttribute.SliderType.Integer, label: "Alt step", description: "Armor blocks added or removed with Alt. Default 500.")]
    public int ArmorStepAlt
    {
        get => armorStepAlt;
        set => SetField(ref armorStepAlt, value);
    }

    [Slider(100, 10000, 100, SliderAttribute.SliderType.Integer, label: "Most per line", description: "Most armor blocks per build list line. Default 1000.")]
    public int ArmorMaxPerLine
    {
        get => armorMaxPerLine;
        set => SetField(ref armorMaxPerLine, value);
    }

    #endregion

    /// <summary>Hands the values to the planner's logic (PlannerSettings), kept in range.</summary>
    public void Apply()
    {
        PlannerSettings.Current = new PlannerSettings
        {
            StepShift = stepShift, StepCtrl = stepCtrl, StepAlt = stepAlt, MaxPerLine = maxPerLine,
            ArmorStepShift = armorStepShift, ArmorStepCtrl = armorStepCtrl, ArmorStepAlt = armorStepAlt, ArmorMaxPerLine = armorMaxPerLine,
            BatchCrafts = batchCrafts, CargoWarnPercent = cargoWarnPercent, NoProgressSeconds = noProgressSeconds,
            KeepListWhenDone = keepListWhenDone, ConfirmClear = confirmClear,
        }.Checked();
    }

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        Apply();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
