using System.Windows;

namespace Morph.Editors.Crop;

/// <summary>Which part of the crop box the pointer holds.</summary>
public enum CropHandle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Crop box geometry (ED-1), in source units (pixels, or points for PDF), top-left origin. The box always stays inside the
/// source; with a ratio (width / height) it keeps that shape.
/// </summary>
public static class CropMath
{
    /// <summary>The largest box of <paramref name="ratio"/> that fits the source, centred; the whole source when null.</summary>
    public static Rect Largest(Size source, double? ratio)
    {
        if (ratio is not { } r)
        {
            return new Rect(source);
        }

        var (w, h) = source.Width / source.Height > r ? (source.Height * r, source.Height) : (source.Width, source.Width / r);
        return new Rect((source.Width - w) / 2, (source.Height - h) / 2, w, h);
    }

    public static Rect Move(Rect box, Vector by, Size source) => box with
    {
        X = Math.Clamp(box.X + by.X, 0, source.Width - box.Width),
        Y = Math.Clamp(box.Y + by.Y, 0, source.Height - box.Height),
    };

    /// <summary>
    /// Drags <paramref name="handle"/> to <paramref name="to"/>. The opposite side or corner stays put (the pointer may cross it,
    /// flipping the box); an edge drag keeps the other axis centred. At least <paramref name="min"/> each way where it fits.
    /// </summary>
    public static Rect Resize(Rect box, CropHandle handle, Point to, Size source, double? ratio, double min)
    {
        to = new Point(Math.Clamp(to.X, 0, source.Width), Math.Clamp(to.Y, 0, source.Height));
        var horizontal = handle is CropHandle.Left or CropHandle.Right or CropHandle.TopLeft or CropHandle.TopRight or CropHandle.BottomLeft or CropHandle.BottomRight;
        var vertical = handle is CropHandle.Top or CropHandle.Bottom or CropHandle.TopLeft or CropHandle.TopRight or CropHandle.BottomLeft or CropHandle.BottomRight;

        // X: the fixed side, or the centre when this drag doesn't move either side. Same for Y.
        var fixedX = handle is CropHandle.Left or CropHandle.TopLeft or CropHandle.BottomLeft ? box.Right : horizontal ? box.Left : box.X + box.Width / 2;
        var fixedY = handle is CropHandle.Top or CropHandle.TopLeft or CropHandle.TopRight ? box.Bottom : vertical ? box.Top : box.Y + box.Height / 2;
        var growsLeft = horizontal && to.X < fixedX;
        var growsUp = vertical && to.Y < fixedY;

        var w = Math.Max(min, horizontal ? Math.Abs(to.X - fixedX) : box.Width);
        var h = Math.Max(min, vertical ? Math.Abs(to.Y - fixedY) : box.Height);
        if (ratio is { } r)
        {
            // An edge drives the other side; a corner follows whichever way the pointer went further.
            if (horizontal && (!vertical || w / h > r))
            {
                h = w / r;
            }
            else
            {
                w = h * r;
            }
        }

        // Never past the source: only the room on the growing side (both sides, when centred).
        var roomW = !horizontal ? 2 * Math.Min(fixedX, source.Width - fixedX) : growsLeft ? fixedX : source.Width - fixedX;
        var roomH = !vertical ? 2 * Math.Min(fixedY, source.Height - fixedY) : growsUp ? fixedY : source.Height - fixedY;
        if (ratio is not null)
        {
            var fit = Math.Min(1, Math.Min(roomW / w, roomH / h));
            (w, h) = (w * fit, h * fit);
        }
        else
        {
            (w, h) = (Math.Min(w, roomW), Math.Min(h, roomH));
        }

        if (w < 1 || h < 1)
        {
            return box; // pushed against the edge with no room left
        }

        var x = !horizontal ? fixedX - w / 2 : growsLeft ? fixedX - w : fixedX;
        var y = !vertical ? fixedY - h / 2 : growsUp ? fixedY - h : fixedY;
        return new Rect(x, y, w, h);
    }
}
