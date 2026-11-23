using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Octopus.Core.Settings;
using TKCode.Arc;
using TKCode.Arc.Themes.Corners;

namespace Octopus.App.Appearance;

/// <summary>
/// Structural dimensions and spacing for each desktop layout style (<see cref="UiDensityValues.Compact"/> vs
/// <see cref="UiDensityValues.Comfortable"/>). Drives both Arc theme tokens and Octopus's own dynamic resources so
/// switching styles updates the entire interface immediately without rebuilding windows.
/// </summary>
public sealed record UiDensityMetrics
{
    public const string PresetSlot = "octopus.density";

    public required string Key { get; init; }
    public required double CornerScale { get; init; }

    // ---- Shell & top bar ----
    public required double TopRowHeight { get; init; }
    public required double IconButtonSize { get; init; }
    public required double TopIconSize { get; init; }
    public required double LogoSize { get; init; }
    public required double SplitterWidth { get; init; }
    public required double DockGap { get; init; }
    public required double TabMaxWidth { get; init; }

    // ---- Project rail ----
    public required double ProjectRailWidth { get; init; }
    public required double ProjectSlotSize { get; init; }
    public required double ProjectChipSize { get; init; }
    public required double ProjectChipPixelSize { get; init; }
    public required double ProjectChipIconSize { get; init; }
    public required double GroupPreviewSize { get; init; }
    public required double GroupCapsuleWidth { get; init; }
    public required double GroupHeaderHeight { get; init; }
    public required double RailSpacing { get; init; }
    public required Thickness RailMargin { get; init; }
    public required Thickness RailBottomMargin { get; init; }
    public required double StatusBadgeSize { get; init; }
    public required CornerRadius StatusBadgeCorner { get; init; }

    // ---- Left project panel & items ----
    public required Thickness PanelGroupMargin { get; init; }
    public required Thickness PanelNameMargin { get; init; }
    public required Thickness PanelPathMargin { get; init; }
    public required Thickness PanelSearchMargin { get; init; }
    public required Thickness PanelListMargin { get; init; }
    public required Thickness PanelHeaderMargin { get; init; }
    public required double ItemRowHeight { get; init; }
    public required Thickness ItemRowMargin { get; init; }
    public required double ItemAgentIconSize { get; init; }
    public required double ItemIconSize { get; init; }
    public required Thickness ItemAgentIconMargin { get; init; }
    public required Thickness ItemIconMargin { get; init; }
    public required Thickness SubagentListMargin { get; init; }
    public required double SubagentListSpacing { get; init; }
    public required Thickness SubagentCardPadding { get; init; }
    public required double SubagentFoldHeight { get; init; }

    // ---- Right explorer panel ----
    public required double ExplorerHeaderHeight { get; init; }
    public required Thickness ExplorerHeaderMargin { get; init; }
    public required Thickness ExplorerSearchMargin { get; init; }
    public required double ExplorerSearchSpacing { get; init; }
    public required double FileTreeRowHeight { get; init; }
    public required double FileTreeIconSize { get; init; }
    public required Thickness FileTreeIconMargin { get; init; }
    public required Thickness GitCommitMargin { get; init; }
    public required double GitCommitSpacing { get; init; }
    public required double GitTextareaHeight { get; init; }
    public required double GitGroupHeight { get; init; }
    public required double GitRowHeight { get; init; }
    public required double GitActionSize { get; init; }
    public required Thickness GitCommitInnerMargin { get; init; }
    public required double GitCommitFileHeight { get; init; }
    public required double SearchRowHeight { get; init; }
    public required double SearchMatchHeight { get; init; }
    public required Thickness SessionAgentMargin { get; init; }
    public required Thickness SessionRowPadding { get; init; }
    public required Thickness TunnelRowPadding { get; init; }

    // ---- File header & launcher ----
    public required double FileHeaderHeight { get; init; }
    public required double LauncherFirstWidth { get; init; }
    public required double LauncherSubWidth { get; init; }
    public required double LauncherRowHeight { get; init; }
    public required double LauncherModelRowHeight { get; init; }
    public required Thickness LauncherSectionMargin { get; init; }
    public required double LauncherSectionSpacing { get; init; }
    public required Thickness LauncherFooterMargin { get; init; }

    // ---- Settings pages ----
    public required double SettingsNavWidth { get; init; }
    public required double SettingsContentMaxWidth { get; init; }
    public required Thickness SettingsContentMargin { get; init; }
    public required double SettingsPageSpacing { get; init; }
    public required Thickness SettingsCardPadding { get; init; }
    public required Thickness SettingsRowPadding { get; init; }
    public required Thickness SettingsAgentHeaderMargin { get; init; }
    public required Thickness SettingsAgentDetailMargin { get; init; }
    public required double SettingsAgentDetailSpacing { get; init; }
    public required double SettingsFieldSpacing { get; init; }

    // ---- Arc design & control tokens ----
    public required double ControlSizeSM { get; init; }
    public required double ControlSizeMD { get; init; }
    public required double ControlSizeLG { get; init; }
    public required double TypeXS { get; init; }
    public required double TypeSM { get; init; }
    public required double TypeBase { get; init; }
    public required double TypeLG { get; init; }
    public required double TypeXL { get; init; }
    public required double DockActionSize { get; init; }
    public required double DockActionGlyphSize { get; init; }
    public required double DockTabHeight { get; init; }
    public required string DockTabInlinePadding { get; init; }
    public required double DockTabFontSize { get; init; }
    public required double DockTabIconSize { get; init; }
    public required double DockTabItemGap { get; init; }
    public required double DockTabGap { get; init; }
    public required double TreeIndentWidth { get; init; }
    public required double TreeRowInlinePadding { get; init; }
    public required double TreeContentGap { get; init; }
    public required double TreeRowGap { get; init; }
    public required string TreePadding { get; init; }
    public required string SegmentedTrackInset { get; init; }
    public required string SegmentedSegmentInset { get; init; }
    public required double SegmentedSegmentGap { get; init; }
    public required string ToggleGroupPadding { get; init; }
    public required double ToggleItemSizeSM { get; init; }
    public required string ToggleItemInsetSM { get; init; }
    public required double ToggleIconSizeSM { get; init; }
    public required double ToggleIconGap { get; init; }
    public required string ButtonInlinePaddingSM { get; init; }
    public required string ButtonInlinePaddingMD { get; init; }
    public required double SelectOptionHeight { get; init; }
    public required string SelectTriggerPadding { get; init; }
    public required double SearchIconSize { get; init; }
    public required double SearchClearSlotSize { get; init; }
    public required double SearchClearIconSize { get; init; }
    public required double InlineEditSlotMinHeight { get; init; }
    public required string InlineEditBodyPadding { get; init; }
    public required string SettingsRowInset { get; init; }
    public required double SettingsRowTextGap { get; init; }
    public required string SettingsRowControlMargin { get; init; }
    public required string SettingsGroupRowsInset { get; init; }
    public required double SidebarRowHeight { get; init; }
    public required double SidebarNestedRowHeight { get; init; }
    public required double SidebarRowFontSize { get; init; }
    public required double SidebarRowGlyphSize { get; init; }
    public required double SidebarSectionGap { get; init; }
    public required double SidebarSectionHeaderHeight { get; init; }
    public required string SidebarPanePadding { get; init; }
    public required string PopoverPanelPadding { get; init; }
    public required string TextareaFramePadding { get; init; }

    /// <summary>Compact layout style: higher information density, tighter margins and rows (system default).</summary>
    public static UiDensityMetrics Compact { get; } = new()
    {
        Key = UiDensityValues.Compact,
        CornerScale = 0.32,

        TopRowHeight = 34,
        IconButtonSize = 24,
        TopIconSize = 14,
        LogoSize = 17,
        SplitterWidth = 3,
        DockGap = 2,
        TabMaxWidth = 168,

        ProjectRailWidth = 46,
        ProjectSlotSize = 36,
        ProjectChipSize = 28,
        ProjectChipPixelSize = 20,
        ProjectChipIconSize = 14,
        GroupPreviewSize = 20,
        GroupCapsuleWidth = 40,
        GroupHeaderHeight = 22,
        RailSpacing = 2,
        RailMargin = new Thickness(0, 6, 0, 4),
        RailBottomMargin = new Thickness(0, 0, 0, 6),
        StatusBadgeSize = 14,
        StatusBadgeCorner = new CornerRadius(7),

        PanelGroupMargin = new Thickness(10, 5, 10, -4),
        PanelNameMargin = new Thickness(4, 4, 6, 0),
        PanelPathMargin = new Thickness(10, 0, 10, 4),
        PanelSearchMargin = new Thickness(6, 0, 6, 4),
        PanelListMargin = new Thickness(4, 0, 4, 6),
        PanelHeaderMargin = new Thickness(6, 2, 0, 0),
        ItemRowHeight = 24,
        ItemRowMargin = new Thickness(6, 0, 6, 0),
        ItemAgentIconSize = 15,
        ItemIconSize = 14,
        ItemAgentIconMargin = new Thickness(0, 0, 6, 0),
        ItemIconMargin = new Thickness(1, 0, 7, 0),
        SubagentListMargin = new Thickness(14, 1, 2, 2),
        SubagentListSpacing = 2,
        SubagentCardPadding = new Thickness(6, 3, 6, 4),
        SubagentFoldHeight = 20,

        ExplorerHeaderHeight = 26,
        ExplorerHeaderMargin = new Thickness(10, 0, 6, 1),
        ExplorerSearchMargin = new Thickness(8, 1, 8, 4),
        ExplorerSearchSpacing = 4,
        FileTreeRowHeight = 23,
        FileTreeIconSize = 14,
        FileTreeIconMargin = new Thickness(0, 0, 6, 0),
        GitCommitMargin = new Thickness(8, 1, 8, 4),
        GitCommitSpacing = 4,
        GitTextareaHeight = 42,
        GitGroupHeight = 23,
        GitRowHeight = 22,
        GitActionSize = 20,
        GitCommitInnerMargin = new Thickness(4, 3, 6, 3),
        GitCommitFileHeight = 21,
        SearchRowHeight = 22,
        SearchMatchHeight = 20,
        SessionAgentMargin = new Thickness(7, 4),
        SessionRowPadding = new Thickness(8, 4, 6, 4),
        TunnelRowPadding = new Thickness(8, 4, 6, 4),

        FileHeaderHeight = 26,
        LauncherFirstWidth = 216,
        LauncherSubWidth = 312,
        LauncherRowHeight = 26,
        LauncherModelRowHeight = 24,
        LauncherSectionMargin = new Thickness(10, 6, 10, 0),
        LauncherSectionSpacing = 7,
        LauncherFooterMargin = new Thickness(10, 8, 10, 8),

        SettingsNavWidth = 196,
        SettingsContentMaxWidth = 920,
        SettingsContentMargin = new Thickness(24, 18, 24, 40),
        SettingsPageSpacing = 10,
        SettingsCardPadding = new Thickness(12, 7),
        SettingsRowPadding = new Thickness(12, 6),
        SettingsAgentHeaderMargin = new Thickness(4, 3, 8, 3),
        SettingsAgentDetailMargin = new Thickness(44, 2, 12, 10),
        SettingsAgentDetailSpacing = 7,
        SettingsFieldSpacing = 6,

        ControlSizeSM = 28,
        ControlSizeMD = 32,
        ControlSizeLG = 38,
        TypeXS = 11,
        TypeSM = 12.5,
        TypeBase = 13.5,
        TypeLG = 15,
        TypeXL = 18,
        DockActionSize = 22,
        DockActionGlyphSize = 13,
        DockTabHeight = 26,
        DockTabInlinePadding = "7,0",
        DockTabFontSize = 12,
        DockTabIconSize = 13,
        DockTabItemGap = 5,
        DockTabGap = 1,
        TreeIndentWidth = 12,
        TreeRowInlinePadding = 5,
        TreeContentGap = 4,
        TreeRowGap = 1,
        TreePadding = "2",
        SegmentedTrackInset = "2",
        SegmentedSegmentInset = "8,3",
        SegmentedSegmentGap = 2,
        ToggleGroupPadding = "2",
        ToggleItemSizeSM = 24,
        ToggleItemInsetSM = "6,0",
        ToggleIconSizeSM = 14,
        ToggleIconGap = 4,
        ButtonInlinePaddingSM = "9,0",
        ButtonInlinePaddingMD = "12,0",
        SelectOptionHeight = 28,
        SelectTriggerPadding = "9,0",
        SearchIconSize = 14,
        SearchClearSlotSize = 20,
        SearchClearIconSize = 13,
        InlineEditSlotMinHeight = 24,
        InlineEditBodyPadding = "6,2",
        SettingsRowInset = "0,9",
        SettingsRowTextGap = 2,
        SettingsRowControlMargin = "12,0,0,0",
        SettingsGroupRowsInset = "12,0",
        SidebarRowHeight = 27,
        SidebarNestedRowHeight = 25,
        SidebarRowFontSize = 12.5,
        SidebarRowGlyphSize = 14,
        SidebarSectionGap = 10,
        SidebarSectionHeaderHeight = 22,
        SidebarPanePadding = "8",
        PopoverPanelPadding = "4",
        TextareaFramePadding = "8,6",
    };

    /// <summary>Comfortable layout style: generous breathing room and taller touch/click surfaces.</summary>
    public static UiDensityMetrics Comfortable { get; } = new()
    {
        Key = UiDensityValues.Comfortable,
        CornerScale = 0.45,

        TopRowHeight = 40,
        IconButtonSize = 28,
        TopIconSize = 16,
        LogoSize = 20,
        SplitterWidth = 4,
        DockGap = 4,
        TabMaxWidth = 200,

        ProjectRailWidth = 54,
        ProjectSlotSize = 42,
        ProjectChipSize = 34,
        ProjectChipPixelSize = 24,
        ProjectChipIconSize = 16,
        GroupPreviewSize = 24,
        GroupCapsuleWidth = 46,
        GroupHeaderHeight = 26,
        RailSpacing = 4,
        RailMargin = new Thickness(0, 10, 0, 6),
        RailBottomMargin = new Thickness(0, 0, 0, 10),
        StatusBadgeSize = 16,
        StatusBadgeCorner = new CornerRadius(8),

        PanelGroupMargin = new Thickness(16, 10, 14, -8),
        PanelNameMargin = new Thickness(8, 10, 8, 0),
        PanelPathMargin = new Thickness(16, 0, 14, 8),
        PanelSearchMargin = new Thickness(10, 0, 10, 6),
        PanelListMargin = new Thickness(6, 0, 6, 8),
        PanelHeaderMargin = new Thickness(8, 6, 0, 0),
        ItemRowHeight = 30,
        ItemRowMargin = new Thickness(8, 0, 8, 0),
        ItemAgentIconSize = 18,
        ItemIconSize = 16,
        ItemAgentIconMargin = new Thickness(0, 0, 8, 0),
        ItemIconMargin = new Thickness(1, 0, 9, 0),
        SubagentListMargin = new Thickness(18, 1, 2, 4),
        SubagentListSpacing = 3,
        SubagentCardPadding = new Thickness(7, 5, 7, 6),
        SubagentFoldHeight = 24,

        ExplorerHeaderHeight = 32,
        ExplorerHeaderMargin = new Thickness(14, 0, 8, 2),
        ExplorerSearchMargin = new Thickness(10, 2, 10, 6),
        ExplorerSearchSpacing = 6,
        FileTreeRowHeight = 30,
        FileTreeIconSize = 16,
        FileTreeIconMargin = new Thickness(0, 0, 8, 0),
        GitCommitMargin = new Thickness(10, 2, 10, 6),
        GitCommitSpacing = 6,
        GitTextareaHeight = 52,
        GitGroupHeight = 30,
        GitRowHeight = 28,
        GitActionSize = 22,
        GitCommitInnerMargin = new Thickness(4, 5, 8, 5),
        GitCommitFileHeight = 26,
        SearchRowHeight = 28,
        SearchMatchHeight = 24,
        SessionAgentMargin = new Thickness(9, 7),
        SessionRowPadding = new Thickness(10, 6, 6, 7),
        TunnelRowPadding = new Thickness(10, 6, 6, 6),

        FileHeaderHeight = 32,
        LauncherFirstWidth = 232,
        LauncherSubWidth = 332,
        LauncherRowHeight = 32,
        LauncherModelRowHeight = 30,
        LauncherSectionMargin = new Thickness(12, 10, 12, 0),
        LauncherSectionSpacing = 10,
        LauncherFooterMargin = new Thickness(12, 10, 12, 10),

        SettingsNavWidth = 230,
        SettingsContentMaxWidth = 820,
        SettingsContentMargin = new Thickness(34, 26, 34, 60),
        SettingsPageSpacing = 16,
        SettingsCardPadding = new Thickness(14, 10),
        SettingsRowPadding = new Thickness(14, 8),
        SettingsAgentHeaderMargin = new Thickness(6, 6, 10, 6),
        SettingsAgentDetailMargin = new Thickness(52, 2, 16, 16),
        SettingsAgentDetailSpacing = 12,
        SettingsFieldSpacing = 10,

        ControlSizeSM = 36,
        ControlSizeMD = 40,
        ControlSizeLG = 46,
        TypeXS = 12,
        TypeSM = 14,
        TypeBase = 15,
        TypeLG = 18,
        TypeXL = 22,
        DockActionSize = 24,
        DockActionGlyphSize = 14,
        DockTabHeight = 28,
        DockTabInlinePadding = "10,0",
        DockTabFontSize = 13,
        DockTabIconSize = 14,
        DockTabItemGap = 6,
        DockTabGap = 2,
        TreeIndentWidth = 16,
        TreeRowInlinePadding = 8,
        TreeContentGap = 6,
        TreeRowGap = 2,
        TreePadding = "4",
        SegmentedTrackInset = "3",
        SegmentedSegmentInset = "12,6",
        SegmentedSegmentGap = 3,
        ToggleGroupPadding = "3",
        ToggleItemSizeSM = 28,
        ToggleItemInsetSM = "8,0",
        ToggleIconSizeSM = 15,
        ToggleIconGap = 5,
        ButtonInlinePaddingSM = "12,0",
        ButtonInlinePaddingMD = "16,0",
        SelectOptionHeight = 34,
        SelectTriggerPadding = "12,0",
        SearchIconSize = 16,
        SearchClearSlotSize = 24,
        SearchClearIconSize = 15,
        InlineEditSlotMinHeight = 30,
        InlineEditBodyPadding = "8,4",
        SettingsRowInset = "0,16",
        SettingsRowTextGap = 4,
        SettingsRowControlMargin = "16,0,0,0",
        SettingsGroupRowsInset = "16,0",
        SidebarRowHeight = 32,
        SidebarNestedRowHeight = 30,
        SidebarRowFontSize = 13.5,
        SidebarRowGlyphSize = 16,
        SidebarSectionGap = 16,
        SidebarSectionHeaderHeight = 26,
        SidebarPanePadding = "12",
        PopoverPanelPadding = "8",
        TextareaFramePadding = "10,8",
    };

    public static UiDensityMetrics For(string? density) =>
        string.Equals(density, UiDensityValues.Comfortable, StringComparison.OrdinalIgnoreCase)
            ? Comfortable
            : Compact;

    public static string Normalize(string? density) =>
        string.Equals(density, UiDensityValues.Comfortable, StringComparison.OrdinalIgnoreCase)
            ? UiDensityValues.Comfortable
            : UiDensityValues.Compact;

    /// <summary>Applies this style's Arc tokens and corner scale to <paramref name="theme"/>.</summary>
    public void ApplyToArc(ArcThemeBuilder theme) =>
        theme.Corners(ArcCornerStyle.Scale(CornerScale))
             .Preset(BuildPreset())
             .Token("DockLayout", "Padding", "0")
             .Token("DockLayout", "Gap", Num(DockGap))
             .Token("DockGroupView", "StripHeight", Num(TopRowHeight))
             // The new-tab menu is two panels wide.
             .Token("PopoverSurface", "PanelMaxWidth", "640");

    private ArcThemePreset BuildPreset() =>
        ArcThemePreset.Create($"{PresetSlot}.{Key}", p =>
        {
            p.ForLight(FillArcTokens);
            p.ForDark(FillArcTokens);
        }, slot: PresetSlot);

    private void FillArcTokens(ArcThemePresetBuilder b)
    {
        b.Token("ControlSizeSM", Num(ControlSizeSM))
         .Token("ControlSizeMD", Num(ControlSizeMD))
         .Token("ControlSizeLG", Num(ControlSizeLG))
         .Token("TypeXS", Num(TypeXS))
         .Token("TypeSM", Num(TypeSM))
         .Token("TypeBase", Num(TypeBase))
         .Token("TypeLG", Num(TypeLG))
         .Token("TypeXL", Num(TypeXL))
         .Token("DockGroupView", "ActionSize", Num(DockActionSize))
         .Token("DockGroupView", "ActionGlyphSize", Num(DockActionGlyphSize))
         .Token("DockTab", "Height", Num(DockTabHeight))
         .Token("DockTab", "InlinePadding", DockTabInlinePadding)
         .Token("DockTab", "FontSize", Num(DockTabFontSize))
         .Token("DockTab", "IconSize", Num(DockTabIconSize))
         .Token("DockTab", "ItemGap", Num(DockTabItemGap))
         .Token("DockTab", "TabGap", Num(DockTabGap))
         .Token("TreeView", "IndentWidth", Num(TreeIndentWidth))
         .Token("TreeView", "RowInlinePadding", Num(TreeRowInlinePadding))
         .Token("TreeView", "ContentGap", Num(TreeContentGap))
         .Token("TreeView", "RowGap", Num(TreeRowGap))
         .Token("TreeView", "TreePadding", TreePadding)
         .Token("SegmentedControl", "TrackInset", SegmentedTrackInset)
         .Token("SegmentedControl", "SegmentInset", SegmentedSegmentInset)
         .Token("SegmentedControl", "SegmentGap", Num(SegmentedSegmentGap))
         .Token("ToggleGroup", "GroupPadding", ToggleGroupPadding)
         .Token("ToggleGroup", "ItemSizeSM", Num(ToggleItemSizeSM))
         .Token("ToggleGroup", "ItemInsetSM", ToggleItemInsetSM)
         .Token("ToggleGroup", "IconSizeSM", Num(ToggleIconSizeSM))
         .Token("ToggleGroup", "IconGap", Num(ToggleIconGap))
         .Token("Button", "InlinePaddingSM", ButtonInlinePaddingSM)
         .Token("Button", "InlinePaddingMD", ButtonInlinePaddingMD)
         .Token("Select", "OptionHeight", Num(SelectOptionHeight))
         .Token("Select", "TriggerPadding", SelectTriggerPadding)
         .Token("SearchField", "SearchIconSize", Num(SearchIconSize))
         .Token("SearchField", "ClearSlotSize", Num(SearchClearSlotSize))
         .Token("SearchField", "ClearIconSize", Num(SearchClearIconSize))
         .Token("InlineEdit", "SlotMinHeight", Num(InlineEditSlotMinHeight))
         .Token("InlineEdit", "BodyPadding", InlineEditBodyPadding)
         .Token("SettingsRow", "RowInset", SettingsRowInset)
         .Token("SettingsRow", "TextGap", Num(SettingsRowTextGap))
         .Token("SettingsRow", "ControlMargin", SettingsRowControlMargin)
         .Token("SettingsGroup", "RowsInset", SettingsGroupRowsInset)
         .Token("Sidebar", "ExpandedWidth", Num(SettingsNavWidth))
         .Token("Sidebar", "RowHeight", Num(SidebarRowHeight))
         .Token("Sidebar", "NestedRowHeight", Num(SidebarNestedRowHeight))
         .Token("Sidebar", "RowFontSize", Num(SidebarRowFontSize))
         .Token("Sidebar", "RowGlyphSize", Num(SidebarRowGlyphSize))
         .Token("Sidebar", "SectionGap", Num(SidebarSectionGap))
         .Token("Sidebar", "SectionHeaderHeight", Num(SidebarSectionHeaderHeight))
         .Token("Sidebar", "PanePadding", SidebarPanePadding)
         .Token("PopoverSurface", "PanelPadding", PopoverPanelPadding)
         .Token("Textarea", "FramePadding", TextareaFramePadding);
    }

    /// <summary>Writes all Octopus dynamic resources for this style into <paramref name="resources"/>.</summary>
    public void WriteResources(IResourceDictionary resources)
    {
        resources["OctopusTopRowHeight"] = TopRowHeight;
        resources["OctopusIconButtonSize"] = IconButtonSize;
        resources["OctopusTopIconSize"] = TopIconSize;
        resources["OctopusLogoSize"] = LogoSize;
        resources["OctopusSplitterWidth"] = SplitterWidth;
        resources["OctopusTabMaxWidth"] = TabMaxWidth;

        resources["OctopusRailWidth"] = ProjectRailWidth;
        resources["OctopusProjectSlotSize"] = ProjectSlotSize;
        resources["OctopusProjectChipSize"] = ProjectChipSize;
        resources["OctopusProjectChipPixelSize"] = ProjectChipPixelSize;
        resources["OctopusProjectChipIconSize"] = ProjectChipIconSize;
        resources["OctopusGroupPreviewSize"] = GroupPreviewSize;
        resources["OctopusGroupCapsuleWidth"] = GroupCapsuleWidth;
        resources["OctopusGroupHeaderHeight"] = GroupHeaderHeight;
        resources["OctopusRailSpacing"] = RailSpacing;
        resources["OctopusRailMargin"] = RailMargin;
        resources["OctopusRailBottomMargin"] = RailBottomMargin;
        resources["OctopusStatusBadgeSize"] = StatusBadgeSize;
        resources["OctopusStatusBadgeCorner"] = StatusBadgeCorner;

        resources["OctopusPanelGroupMargin"] = PanelGroupMargin;
        resources["OctopusPanelNameMargin"] = PanelNameMargin;
        resources["OctopusPanelPathMargin"] = PanelPathMargin;
        resources["OctopusPanelSearchMargin"] = PanelSearchMargin;
        resources["OctopusPanelListMargin"] = PanelListMargin;
        resources["OctopusPanelHeaderMargin"] = PanelHeaderMargin;
        resources["OctopusItemRowHeight"] = ItemRowHeight;
        resources["OctopusItemRowMargin"] = ItemRowMargin;
        resources["OctopusItemAgentIconSize"] = ItemAgentIconSize;
        resources["OctopusItemIconSize"] = ItemIconSize;
        resources["OctopusItemAgentIconMargin"] = ItemAgentIconMargin;
        resources["OctopusItemIconMargin"] = ItemIconMargin;
        resources["OctopusSubagentListMargin"] = SubagentListMargin;
        resources["OctopusSubagentListSpacing"] = SubagentListSpacing;
        resources["OctopusSubagentCardPadding"] = SubagentCardPadding;
        resources["OctopusSubagentFoldHeight"] = SubagentFoldHeight;

        resources["OctopusExplorerHeaderHeight"] = ExplorerHeaderHeight;
        resources["OctopusExplorerHeaderMargin"] = ExplorerHeaderMargin;
        resources["OctopusExplorerSearchMargin"] = ExplorerSearchMargin;
        resources["OctopusExplorerSearchSpacing"] = ExplorerSearchSpacing;
        resources["OctopusFileTreeRowHeight"] = FileTreeRowHeight;
        resources["OctopusFileTreeIconSize"] = FileTreeIconSize;
        resources["OctopusFileTreeIconMargin"] = FileTreeIconMargin;
        resources["OctopusGitCommitMargin"] = GitCommitMargin;
        resources["OctopusGitCommitSpacing"] = GitCommitSpacing;
        resources["OctopusGitTextareaHeight"] = GitTextareaHeight;
        resources["OctopusGitGroupHeight"] = GitGroupHeight;
        resources["OctopusGitRowHeight"] = GitRowHeight;
        resources["OctopusGitActionSize"] = GitActionSize;
        resources["OctopusGitCommitInnerMargin"] = GitCommitInnerMargin;
        resources["OctopusGitCommitFileHeight"] = GitCommitFileHeight;
        resources["OctopusSearchRowHeight"] = SearchRowHeight;
        resources["OctopusSearchMatchHeight"] = SearchMatchHeight;
        resources["OctopusSessionAgentMargin"] = SessionAgentMargin;
        resources["OctopusSessionRowPadding"] = SessionRowPadding;
        resources["OctopusTunnelRowPadding"] = TunnelRowPadding;

        resources["OctopusFileHeaderHeight"] = FileHeaderHeight;
        resources["OctopusLauncherFirstWidth"] = LauncherFirstWidth;
        resources["OctopusLauncherSubWidth"] = LauncherSubWidth;
        resources["OctopusLauncherRowHeight"] = LauncherRowHeight;
        resources["OctopusLauncherModelRowHeight"] = LauncherModelRowHeight;
        resources["OctopusLauncherSectionMargin"] = LauncherSectionMargin;
        resources["OctopusLauncherSectionSpacing"] = LauncherSectionSpacing;
        resources["OctopusLauncherFooterMargin"] = LauncherFooterMargin;

        resources["OctopusSettingsNavWidth"] = SettingsNavWidth;
        resources["OctopusSettingsContentMaxWidth"] = SettingsContentMaxWidth;
        resources["OctopusSettingsContentMargin"] = SettingsContentMargin;
        resources["OctopusSettingsPageSpacing"] = SettingsPageSpacing;
        resources["OctopusSettingsCardPadding"] = SettingsCardPadding;
        resources["OctopusSettingsRowPadding"] = SettingsRowPadding;
        resources["OctopusSettingsAgentHeaderMargin"] = SettingsAgentHeaderMargin;
        resources["OctopusSettingsAgentDetailMargin"] = SettingsAgentDetailMargin;
        resources["OctopusSettingsAgentDetailSpacing"] = SettingsAgentDetailSpacing;
        resources["OctopusSettingsFieldSpacing"] = SettingsFieldSpacing;
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
