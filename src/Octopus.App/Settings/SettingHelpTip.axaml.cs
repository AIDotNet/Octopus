using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using ArcTooltip = TKCode.Arc.Tooltip;

namespace Octopus.App.Settings;

/// <summary>
/// A quiet circular <c>?</c> icon that stays out of the way until hovered or focused, when it opens an Arc tooltip
/// with the full description of a setting or section. Collapses automatically via the <c>:empty</c> pseudoclass while
/// <see cref="Tip"/> is empty so callers can still bind <see cref="Visual.IsVisibleProperty"/> freely.
/// </summary>
[PseudoClasses(":empty")]
public sealed partial class SettingHelpTip : UserControl
{
    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<SettingHelpTip, string?>(nameof(Tip));

    static SettingHelpTip()
    {
        TipProperty.Changed.AddClassHandler<SettingHelpTip>((control, e) => control.ApplyTip(e.GetNewValue<string?>()));
    }

    public SettingHelpTip()
    {
        InitializeComponent();
        ApplyTip(Tip);
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    private void ApplyTip(string? tip)
    {
        var text = string.IsNullOrWhiteSpace(tip) ? null : tip.Trim();
        PseudoClasses.Set(":empty", text is null);
        if (HitTarget is not null)
        {
            ArcTooltip.SetTip(HitTarget, text);
            AutomationProperties.SetName(HitTarget, text ?? "");
        }
        AutomationProperties.SetHelpText(this, text ?? "");
    }
}
