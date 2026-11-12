using Avalonia;
using Avalonia.Controls;

namespace Octopus.App.Settings.Agents;

/// <summary>The layout of one line of an agent's details: the label on the left, the control (and its description) on the right.</summary>
public sealed class FieldRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FieldRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<FieldRow, string?>(nameof(Description));

    /// <summary>Top-aligns the label, for controls that are taller than one line (tags, lists, multi-line text).</summary>
    public static readonly StyledProperty<bool> TopAlignedProperty =
        AvaloniaProperty.Register<FieldRow, bool>(nameof(TopAligned));

    static FieldRow()
    {
        TopAlignedProperty.Changed.AddClassHandler<FieldRow>((row, e) => row.PseudoClasses.Set(":top", e.GetNewValue<bool>()));
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool TopAligned
    {
        get => GetValue(TopAlignedProperty);
        set => SetValue(TopAlignedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(FieldRow);
}
