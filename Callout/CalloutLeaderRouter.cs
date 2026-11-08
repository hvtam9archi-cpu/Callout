using System;
using System.Collections.Generic;

namespace Callout.Logic
{
    internal static class CalloutLeaderRouter
    {
        private const double Tolerance = 1.0e-6;

        public static List<ClipPoint> CreateRoute(IReadOnlyList<ClipPoint> source, IReadOnlyList<ClipPoint> detail)
        {
            var sourcePolygon = new CalloutClipPolygon(source);
            var detailPolygon = new CalloutClipPolygon(detail);
            var sourceBounds = new Bounds(source);
            var detailBounds = new Bounds(detail);
            // An overlapping/touching detail has no clear gap for a connector.
            if (Overlaps(source, detail, sourcePolygon, detailPolygon)) return new List<ClipPoint>();

            var starts = Anchors(sourcePolygon, sourceBounds, detailBounds);
            var ends = Anchors(detailPolygon, detailBounds, sourceBounds);
            double spacing = Math.Max(Tolerance * 10, Math.Min(sourceBounds.Size, detailBounds.Size) * 0.1);
            double middleX = sourceBounds.MaxX < detailBounds.MinX ? (sourceBounds.MaxX + detailBounds.MinX) / 2 :
                detailBounds.MaxX < sourceBounds.MinX ? (detailBounds.MaxX + sourceBounds.MinX) / 2 :
                (sourceBounds.Center.X + detailBounds.Center.X) / 2;
            double middleY = sourceBounds.MaxY < detailBounds.MinY ? (sourceBounds.MaxY + detailBounds.MinY) / 2 :
                detailBounds.MaxY < sourceBounds.MinY ? (detailBounds.MaxY + sourceBounds.MinY) / 2 :
                (sourceBounds.Center.Y + detailBounds.Center.Y) / 2;
            double[] xLanes = { middleX, Math.Min(sourceBounds.MinX, detailBounds.MinX) - spacing,
                Math.Max(sourceBounds.MaxX, detailBounds.MaxX) + spacing };
            double[] yLanes = { middleY, Math.Min(sourceBounds.MinY, detailBounds.MinY) - spacing,
                Math.Max(sourceBounds.MaxY, detailBounds.MaxY) + spacing };

            var best = new List<ClipPoint>();
            double bestCost = double.MaxValue;
            foreach (ClipPoint start in starts)
            foreach (ClipPoint end in ends)
            {
                Consider(new[] { start, end }, sourcePolygon, detailPolygon, spacing, ref best, ref bestCost);
                Consider(new[] { start, new ClipPoint(end.X, start.Y), end }, sourcePolygon, detailPolygon, spacing, ref best, ref bestCost);
                Consider(new[] { start, new ClipPoint(start.X, end.Y), end }, sourcePolygon, detailPolygon, spacing, ref best, ref bestCost);
                foreach (double x in xLanes)
                    Consider(new[] { start, new ClipPoint(x, start.Y), new ClipPoint(x, end.Y), end },
                        sourcePolygon, detailPolygon, spacing, ref best, ref bestCost);
                foreach (double y in yLanes)
                    Consider(new[] { start, new ClipPoint(start.X, y), new ClipPoint(end.X, y), end },
                        sourcePolygon, detailPolygon, spacing, ref best, ref bestCost);
            }
            return best;
        }

        private static void Consider(IReadOnlyList<ClipPoint> candidate, CalloutClipPolygon source,
            CalloutClipPolygon detail, double bendPenalty, ref List<ClipPoint> best, ref double bestCost)
        {
            var route = new List<ClipPoint>();
            foreach (ClipPoint point in candidate)
            {
                if (route.Count > 0 && Distance(route[route.Count - 1], point) <= Tolerance) continue;
                route.Add(point);
                while (route.Count >= 3)
                {
                    int last = route.Count - 1;
                    ClipPoint a = route[last - 2], b = route[last - 1], c = route[last];
                    bool sameDirection = (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y) >= 0;
                    if (sameDirection && ((Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(b.X - c.X) <= Tolerance) ||
                        (Math.Abs(a.Y - b.Y) <= Tolerance && Math.Abs(b.Y - c.Y) <= Tolerance)))
                        route.RemoveAt(last - 1);
                    else break;
                }
            }
            if (route.Count < 2) return;
            double cost = bendPenalty * (route.Count - 2);
            for (int i = 1; i < route.Count; i++)
            {
                ClipPoint a = route[i - 1], b = route[i];
                if (Math.Abs(a.X - b.X) > Tolerance && Math.Abs(a.Y - b.Y) > Tolerance) return;
                cost += Distance(a, b);
            }
            if (cost >= bestCost - Tolerance) return;
            for (int i = 1; i < route.Count; i++)
            {
                if (!Clear(route[i - 1], route[i], source) || !Clear(route[i - 1], route[i], detail)) return;
            }
            best = route;
            bestCost = cost;
        }

        private static bool Clear(ClipPoint start, ClipPoint end, CalloutClipPolygon polygon)
        {
            var cuts = polygon.GetBoundaryParameters(start, end);
            cuts.Add(0.0);
            cuts.Add(1.0);
            cuts.Sort();
            for (int i = 1; i < cuts.Count; i++)
            {
                if ((cuts[i] - cuts[i - 1]) * Distance(start, end) <= Tolerance) continue;
                double t = (cuts[i] + cuts[i - 1]) / 2;
                if (polygon.Contains(new ClipPoint(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t)))
                    return false;
            }
            return true;
        }

        private static List<ClipPoint> Anchors(CalloutClipPolygon polygon, Bounds bounds, Bounds other)
        {
            var points = new List<ClipPoint>();
            double margin = Math.Max(bounds.Size, 1.0);
            AddCrossings(points, polygon, new ClipPoint(bounds.MinX - margin, bounds.Center.Y), new ClipPoint(bounds.MaxX + margin, bounds.Center.Y));
            AddCrossings(points, polygon, new ClipPoint(bounds.Center.X, bounds.MinY - margin), new ClipPoint(bounds.Center.X, bounds.MaxY + margin));
            double minY = Math.Max(bounds.MinY, other.MinY), maxY = Math.Min(bounds.MaxY, other.MaxY);
            if (maxY > minY)
                AddCrossings(points, polygon, new ClipPoint(bounds.MinX - margin, (minY + maxY) / 2), new ClipPoint(bounds.MaxX + margin, (minY + maxY) / 2));
            double minX = Math.Max(bounds.MinX, other.MinX), maxX = Math.Min(bounds.MaxX, other.MaxX);
            if (maxX > minX)
                AddCrossings(points, polygon, new ClipPoint((minX + maxX) / 2, bounds.MinY - margin), new ClipPoint((minX + maxX) / 2, bounds.MaxY + margin));
            double length = Distance(bounds.Center, other.Center);
            if (length > Tolerance)
            {
                double x = (other.Center.X - bounds.Center.X) * margin * 2 / length;
                double y = (other.Center.Y - bounds.Center.Y) * margin * 2 / length;
                AddCrossings(points, polygon, new ClipPoint(bounds.Center.X - x, bounds.Center.Y - y),
                    new ClipPoint(bounds.Center.X + x, bounds.Center.Y + y));
            }
            return points;
        }

        private static void AddCrossings(List<ClipPoint> anchors, CalloutClipPolygon polygon, ClipPoint start, ClipPoint end)
        {
            var cuts = polygon.GetBoundaryParameters(start, end);
            if (cuts.Count == 0) return;
            foreach (double t in new[] { cuts[0], cuts[cuts.Count - 1] })
            {
                var point = new ClipPoint(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
                if (!anchors.Exists(existing => Distance(existing, point) <= Tolerance)) anchors.Add(point);
            }
        }

        private static bool Overlaps(IReadOnlyList<ClipPoint> source, IReadOnlyList<ClipPoint> detail,
            CalloutClipPolygon sourcePolygon, CalloutClipPolygon detailPolygon)
        {
            foreach (ClipPoint point in source) if (detailPolygon.Contains(point)) return true;
            foreach (ClipPoint point in detail) if (sourcePolygon.Contains(point)) return true;
            for (int i = 0; i < source.Count; i++)
                if (detailPolygon.GetBoundaryParameters(source[i], source[(i + 1) % source.Count]).Count > 0) return true;
            return false;
        }

        private static double Distance(ClipPoint a, ClipPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private sealed class Bounds
        {
            public double MinX { get; private set; } = double.MaxValue;
            public double MinY { get; private set; } = double.MaxValue;
            public double MaxX { get; private set; } = double.MinValue;
            public double MaxY { get; private set; } = double.MinValue;
            public ClipPoint Center => new ClipPoint((MinX + MaxX) / 2, (MinY + MaxY) / 2);
            public double Size => Math.Max(MaxX - MinX, MaxY - MinY);

            public Bounds(IReadOnlyList<ClipPoint> points)
            {
                foreach (ClipPoint point in points)
                {
                    MinX = Math.Min(MinX, point.X); MinY = Math.Min(MinY, point.Y);
                    MaxX = Math.Max(MaxX, point.X); MaxY = Math.Max(MaxY, point.Y);
                }
            }
        }
    }
}
