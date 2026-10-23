using Windows.Graphics;

namespace RDesk.Helpers;

/// <summary>
/// Magnetic alignment for desktop file boxes: when a box is dragged near another,
/// edges line up and neighbors sit flush with a small gap (DeskBox-style snap).
/// </summary>
public static class BoxSnap
{
    public const int Threshold = 16;
    public const int Gap = 8;

    public static RectInt32 Snap(RectInt32 proposed, IEnumerable<RectInt32> others, RectInt32? workArea = null)
    {
        var x = proposed.X;
        var y = proposed.Y;
        var w = proposed.Width;
        var h = proposed.Height;
        var bestX = Threshold + 1;
        var bestY = Threshold + 1;
        int? snapX = null;
        int? snapY = null;

        void TryX(int target)
        {
            var dist = Math.Abs(x - target);
            if (dist <= Threshold && dist < bestX)
            {
                bestX = dist;
                snapX = target;
            }
        }

        void TryY(int target)
        {
            var dist = Math.Abs(y - target);
            if (dist <= Threshold && dist < bestY)
            {
                bestY = dist;
                snapY = target;
            }
        }

        foreach (var o in others)
        {
            if (o.Width <= 0 || o.Height <= 0)
            {
                continue;
            }

            TryX(o.X);
            TryX(o.X + o.Width - w);
            TryX(o.X + o.Width + Gap);
            TryX(o.X - w - Gap);

            TryY(o.Y);
            TryY(o.Y + o.Height - h);
            TryY(o.Y + o.Height + Gap);
            TryY(o.Y - h - Gap);
        }

        if (workArea is { Width: > 0, Height: > 0 } area)
        {
            TryX(area.X);
            TryX(area.X + area.Width - w);
            TryY(area.Y);
            TryY(area.Y + area.Height - h);
        }

        return new RectInt32(snapX ?? x, snapY ?? y, w, h);
    }
}
