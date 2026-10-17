using System;

namespace DalamudMCP.Tools;

/// <summary>Native ScreenX already includes origin and ancestor transforms.</summary>
internal readonly record struct ScreenNode(float X, float Width, float M11, float M12, float M21, bool Visible);

internal static class NodeScreenMatch
{
    public static bool RotationMatches(double radians, double minimum, double maximum) =>
        double.IsFinite(radians) && double.IsFinite(minimum) && double.IsFinite(maximum) &&
        minimum >= -Math.PI && maximum <= Math.PI && minimum <= maximum && radians >= minimum && radians <= maximum;

    public static bool AnchorsMatch(ScreenNode observed, ScreenNode marker, double fraction, double observedAnchor, double targetAnchor)
    {
        if (!double.IsFinite(observedAnchor) || observedAnchor is < 0 or > 1 ||
            !double.IsFinite(targetAnchor) || targetAnchor is < 0 or > 1 ||
            !double.IsFinite(fraction) || fraction <= 0 || fraction > 1 || !Valid(observed) || !Valid(marker)) return false;
        var point = (double)observed.X + observed.Width * observed.M11 * observedAnchor;
        var target = (double)marker.X + marker.Width * marker.M11 * targetAnchor;
        return Math.Abs(point - target) <= marker.Width * marker.M11 * fraction / 2;
    }

    public static bool Matches(ScreenNode observed, ScreenNode marker, double fraction)
    {
        if (!double.IsFinite(fraction) || fraction <= 0 || fraction > 1 || !Valid(observed) || !Valid(marker)) return false;
        var right = (double)observed.X + observed.Width * observed.M11;
        var markerWidth = (double)marker.Width * marker.M11;
        var center = marker.X + markerWidth / 2;
        return Math.Abs(right - center) <= markerWidth * fraction / 2;
    }

    private static bool Valid(ScreenNode node) => node.Visible && float.IsFinite(node.X) &&
        float.IsFinite(node.Width) && node.Width > 0 && float.IsFinite(node.M11) && node.M11 > 0 &&
        float.IsFinite(node.M12) && Math.Abs(node.M12) < 0.0001f &&
        float.IsFinite(node.M21) && Math.Abs(node.M21) < 0.0001f;
}
