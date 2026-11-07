using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal readonly struct OutlineSegment
    {
        internal readonly float X, Y, Width, Height;
        internal OutlineSegment(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    internal static class OutlineGeometry
    {
        internal static List<OutlineSegment> Build(bool[] cells, int width, int height, float cell, bool dashed)
        {
            var segments = new List<OutlineSegment>();
            if (cells == null || width < 1 || height < 1 || width > 64 || height > 64 ||
                cells.Length != width * height || cell <= 0 || float.IsNaN(cell) || float.IsInfinity(cell))
                return segments;
            const float line = 1.25f;
            bool Filled(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && cells[y * width + x];
            void Edge(float x, float y, bool horizontal)
            {
                float length = cell;
                float piece = dashed ? length * 0.38f : length;
                segments.Add(horizontal ? new OutlineSegment(x, y, piece, line) : new OutlineSegment(x, y, line, piece));
                if (dashed)
                {
                    float offset = length - piece;
                    segments.Add(horizontal ? new OutlineSegment(x + offset, y, piece, line) :
                        new OutlineSegment(x, y - offset, line, piece));
                }
            }
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (!Filled(x, y)) continue;
                    float px = x * cell, py = -y * cell;
                    if (!Filled(x, y - 1)) Edge(px, py, true);
                    if (!Filled(x, y + 1)) Edge(px, py - cell + line, true);
                    if (!Filled(x - 1, y)) Edge(px, py, false);
                    if (!Filled(x + 1, y)) Edge(px + cell - line, py, false);
                }
            return segments;
        }
    }
}
