// From CometWorks/client2-plugin-template (MIT, (c) 2026 Viktor Ferenczi), used unchanged unless noted.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ClientPlugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class SliderAttribute : Attribute, IElement
{
    public enum SliderType
    {
        Integer,
        Float,
    }

    public readonly double Min;
    public readonly double Max;
    public readonly double Step;
    public readonly SliderType Type;
    public readonly string Label;
    public readonly string Description;

    public SliderAttribute(double min, double max, double step = 1.0, SliderType type = SliderType.Float, string label = null, string description = null)
    {
        Min = min;
        Max = max;
        Step = step;
        Type = type;
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var slider = new Slider
        {
            Minimum = Min,
            Maximum = Max,
            TickFrequency = Step,
            IsSnapToTickEnabled = true,
            Width = 240,
            VerticalAlignment = VerticalAlignment.Center,
            Value = Convert.ToDouble(getter()),
        };

        slider.ValueChanged += (_, _) =>
        {
            if (Type == SliderType.Integer)
                setter((int)Math.Round(slider.Value));
            else
                setter((float)slider.Value);
            if (Type == SliderType.Integer) ShowWhole(slider);
        };
        if (Type == SliderType.Integer) slider.Loaded += (_, _) => ShowWhole(slider);

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, slider);
    }

    // LW Build Planner: the game's slider template shows the value with two decimals ("50.00"); whole-number settings
    // show "50". The template's number text is found once loaded and given the whole number (a local value wins over
    // the template's binding). Nothing found (other template): the template's text stays.
    private static void ShowWhole(Slider slider)
    {
        try
        {
            var whole = ((int)Math.Round(slider.Value)).ToString(CultureInfo.CurrentCulture);
            foreach (var text in slider.GetVisualDescendants().OfType<TextBlock>()
                         .Where(t => double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                                     || double.TryParse(t.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _)))
                text.Text = whole;
        }
        catch (Exception) { /* only the look */ }
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(int), typeof(float), typeof(double) };
}
