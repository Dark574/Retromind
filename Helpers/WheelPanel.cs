using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Retromind.Helpers;

/// <summary>
/// A custom panel that arranges its children in a vertical wheel layout.
/// </summary>
public class WheelPanel : Panel
{
    // --- Attached Properties to control the Wheel from XAML ---

    public static readonly AttachedProperty<int> SelectedItemIndexProperty =
        AvaloniaProperty.RegisterAttached<WheelPanel, Control, int>("SelectedItemIndex", -1);

    public static int GetSelectedItemIndex(Control element) => element.GetValue(SelectedItemIndexProperty);
    public static void SetSelectedItemIndex(Control element, int value) => element.SetValue(SelectedItemIndexProperty, value);

    public static readonly AttachedProperty<double> WheelRadiusProperty =
        AvaloniaProperty.RegisterAttached<WheelPanel, Control, double>("WheelRadius", 400.0);
    
    public static double GetWheelRadius(Control element) => element.GetValue(WheelRadiusProperty);
    public static void SetWheelRadius(Control element, double value) => element.SetValue(WheelRadiusProperty, value);
    
    public static readonly AttachedProperty<double> ItemSpacingAngleProperty =
        AvaloniaProperty.RegisterAttached<WheelPanel, Control, double>("ItemSpacingAngle", 20.0);
    
    public static double GetItemSpacingAngle(Control element) => element.GetValue(ItemSpacingAngleProperty);
    public static void SetItemSpacingAngle(Control element, double value) => element.SetValue(ItemSpacingAngleProperty, value);

    public static readonly AttachedProperty<double> OffsetXProperty =
        AvaloniaProperty.RegisterAttached<WheelPanel, Control, double>("OffsetX", 0.0);

    public static double GetOffsetX(Control element) => element.GetValue(OffsetXProperty);
    public static void SetOffsetX(Control element, double value) => element.SetValue(OffsetXProperty, value);

    static WheelPanel()
    {
        // Whenever our custom properties change, we need to re-arrange the layout.
        AffectsArrange<WheelPanel>(SelectedItemIndexProperty, WheelRadiusProperty, ItemSpacingAngleProperty, OffsetXProperty);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        if (children.Count == 0) return finalSize;
        
        var selectedIndex = GetSelectedItemIndex(this);
        var radius = GetWheelRadius(this);
        var spacingAngle = GetItemSpacingAngle(this);
        var offsetX = GetOffsetX(this);
        
        // Center of the wheel
        // Y axis is centered.
        // X axis is shifted left by the radius so the circle edge starts at x=0.
        var wheelCenterX = -radius + offsetX;
        var wheelCenterY = finalSize.Height / 2;

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child == null) continue;

            // --- Calculate position and transformations ---
            
            // Difference from the selected item
            var delta = i - selectedIndex;
            
            // Angle in degrees. The selected item is at 90 degrees (right-most point on the circle).
            var angleDeg = 0 - (delta * spacingAngle);
            var angleRad = angleDeg * (Math.PI / 180.0);

            // Calculate X and Y on the circle
            var x = wheelCenterX + radius * Math.Cos(angleRad);
            var y = wheelCenterY - radius * Math.Sin(angleRad);

            // Center the item on the calculated point
            var itemX = x - (child.DesiredSize.Width / 2);
            var itemY = y - (child.DesiredSize.Height / 2);
            
            // --- Apply transformations for a 3D effect ---

            // Items further away get smaller and more transparent
            var distanceFactor = Math.Abs(delta);
            var scale = Math.Max(0.4, 1.0 - distanceFactor * 0.15); // Don't shrink below 40%
            var opacity = Math.Max(0, 1.0 - distanceFactor * 0.25); // Fade out completely

            // The selected item should be fully opaque and on top
            if (i == selectedIndex)
            {
                scale = 1.0;
                opacity = 1.0;
                child.ZIndex = 100; // Bring to front
            }
            else
            {
                child.ZIndex = 100 - distanceFactor; // Further items go to the back
            }

            // Apply transformations via RenderTransform
            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(scale, scale));
            child.RenderTransform = transformGroup;
            child.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            child.Opacity = opacity;

            // Arrange the child at its final position
            child.Arrange(new Rect(new Point(itemX, itemY), child.DesiredSize));
        }

        return finalSize;
    }
}
