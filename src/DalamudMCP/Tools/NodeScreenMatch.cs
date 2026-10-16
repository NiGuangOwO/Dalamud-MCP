using System;

namespace DalamudMCP.Tools;

/// <summary>Native ScreenX already includes origin and ancestor transforms.</summary>
internal readonly record struct ScreenNode(float X, float Width, float M11, float M12, float M21, bool Visible);

internal static class NodeScreenMatch
{
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
