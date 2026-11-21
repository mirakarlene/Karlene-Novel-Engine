using System.Windows;

namespace KNE;

internal static class MouseGesture
{
    internal static string? Identify(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return null;
        var endToEnd = points[^1] - points[0];
        double distance = endToEnd.Length, length = 0;
        for (int index = 1; index < points.Count; index++) length += (points[index] - points[index - 1]).Length;
        if (distance >= 40 && length <= distance * 1.25)
        {
            if (Math.Abs(endToEnd.X) >= Math.Abs(endToEnd.Y) * 1.6) return endToEnd.X > 0 ? "right" : "left";
            if (Math.Abs(endToEnd.Y) >= Math.Abs(endToEnd.X) * 1.6) return endToEnd.Y > 0 ? "down" : "up";
        }
        if (points.Count < 9) return null;
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double width = maxX - minX, height = maxY - minY;
        double diameter = Math.Min(width, height);
        if (diameter < 28 || Math.Max(width, height) > diameter * 1.7 || distance > diameter * .33)
            return null;
        var center = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        double rotation = 0, totalTurn = 0;
        for (int index = 1; index < points.Count; index++)
        {
            var from = points[index - 1] - center;
            var to = points[index] - center;
            if (to.Length < diameter * .2 || from.Length < diameter * .2) return null;
            double angle = Vector.AngleBetween(from, to);
            rotation += angle;
            totalTurn += Math.Abs(angle);
        }
        return Math.Abs(rotation) is >= 295 and <= 425 && Math.Abs(rotation) >= totalTurn * .75 ? "circle" : null;
    }
}
