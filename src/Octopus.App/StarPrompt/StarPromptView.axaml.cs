using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;

namespace Octopus.App.StarPrompt;

/// <summary>
/// Scheme C floating star reminder pill at the bottom right. Animates gently on entry and lifts the window's toast
/// stack while open so toasts never overlap it.
/// </summary>
public partial class StarPromptView : UserControl
{
    private const double ToastGap = 12;
    private bool _liftedToasts;

    public StarPromptView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible) AnimateEntrance();
                LiftToasts();
            }
            else if (e.Property == BoundsProperty)
            {
                LiftToasts();
            }
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_liftedToasts) SetToastOffset(0);
    }

    private void AnimateEntrance()
    {
        if (PillContainer.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            PillContainer.RenderTransform = translate;
        }
        PillContainer.Transitions = null;
        translate.Transitions = null;
        PillContainer.Opacity = 0;
        translate.Y = 12;

        PillContainer.Transitions =
        [
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new CubicEaseOut(),
            },
        ];
        translate.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(240),
                Easing = new CubicEaseOut(),
            },
        ];

        PillContainer.Opacity = 1;
        translate.Y = 0;
    }

    private void LiftToasts()
    {
        if (IsVisible && Bounds.Height > 0)
        {
            SetToastOffset(Bounds.Height + ToastGap);
            _liftedToasts = true;
        }
        else if (_liftedToasts)
        {
            // Only clear the offset when we were the control that set it, so we never stomp UpdateCardView's lift.
            SetToastOffset(0);
            _liftedToasts = false;
        }
    }

    private void SetToastOffset(double offset)
    {
        if (TopLevel.GetTopLevel(this) is null) return;
        var stack = ToastHost.GetStack(this);
        if (stack is null) return;
        var margin = stack.Margin;
        if (margin.Bottom != offset) stack.Margin = new Thickness(margin.Left, margin.Top, margin.Right, offset);
    }
}
