using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Octopus.App.Settings;

/// <summary>
/// Attaches a hover <see cref="SettingHelpTip"/> beside the header of a <see cref="SettingsRow"/> so the setting's
/// detailed description stays hidden by default and appears when the user hovers the <c>?</c> icon.
/// </summary>
public static class SettingHelp
{
    private const string HeaderHostName = "PART_HeaderWithHelp";

    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tip", typeof(SettingHelp));

    private static readonly AttachedProperty<bool> IsHookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsHooked", typeof(SettingHelp));

    private static readonly AttachedProperty<SettingHelpTip?> ActiveTipControlProperty =
        AvaloniaProperty.RegisterAttached<Control, SettingHelpTip?>("ActiveTipControl", typeof(SettingHelp));

    static SettingHelp()
    {
        TipProperty.Changed.AddClassHandler<SettingsRow>(OnSettingsRowTipChanged);
    }

    public static string? GetTip(Control element) => element.GetValue(TipProperty);

    public static void SetTip(Control element, string? value) => element.SetValue(TipProperty, value);

    private static void OnSettingsRowTipChanged(SettingsRow row, AvaloniaPropertyChangedEventArgs e)
    {
        var tip = e.GetNewValue<string?>();
        AutomationProperties.SetHelpText(row, tip ?? "");

        if (!row.GetValue(IsHookedProperty))
        {
            row.SetValue(IsHookedProperty, true);
            row.TemplateApplied += OnRowTemplateApplied;
        }

        if (row.GetValue(ActiveTipControlProperty) is { } existing)
        {
            existing.Tip = tip;
            return;
        }

        if (row.FindHeaderTextBlock() is { } header)
        {
            AttachHelpTip(row, header, tip);
        }
    }

    private static void OnRowTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is not SettingsRow row)
        {
            return;
        }

        var header = e.NameScope.Find<TextBlock>("PART_Header") ?? row.FindHeaderTextBlock();
        if (header is not null)
        {
            AttachHelpTip(row, header, GetTip(row));
        }
    }

    private static TextBlock? FindHeaderTextBlock(this SettingsRow row) =>
        row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_Header");

    private static void AttachHelpTip(SettingsRow row, TextBlock header, string? tip)
    {
        if (header.Parent is Grid { Name: HeaderHostName } existingHost)
        {
            var existingTip = existingHost.Children.OfType<SettingHelpTip>().FirstOrDefault();
            if (existingTip is not null)
            {
                existingTip.Tip = tip;
                row.SetValue(ActiveTipControlProperty, existingTip);
                return;
            }
        }

        if (header.Parent is not Panel parent)
        {
            return;
        }

        var index = parent.Children.IndexOf(header);
        if (index < 0)
        {
            return;
        }

        var gridRow = Grid.GetRow(header);
        var gridCol = Grid.GetColumn(header);
        var gridRowSpan = Grid.GetRowSpan(header);
        var gridColSpan = Grid.GetColumnSpan(header);
        var margin = header.Margin;
        var vAlign = header.VerticalAlignment;

        var helpTip = new SettingHelpTip
        {
            Tip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var host = new Grid
        {
            Name = HeaderHostName,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 5,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = vAlign,
            Margin = margin,
        };
        Grid.SetRow(host, gridRow);
        Grid.SetColumn(host, gridCol);
        Grid.SetRowSpan(host, gridRowSpan);
        Grid.SetColumnSpan(host, gridColSpan);

        parent.Children.RemoveAt(index);

        header.Margin = default;
        header.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(header, 0);
        Grid.SetColumn(header, 0);
        Grid.SetRowSpan(header, 1);
        Grid.SetColumnSpan(header, 1);

        Grid.SetColumn(helpTip, 1);

        host.Children.Add(header);
        host.Children.Add(helpTip);
        parent.Children.Insert(index, host);

        row.SetValue(ActiveTipControlProperty, helpTip);
    }
}
