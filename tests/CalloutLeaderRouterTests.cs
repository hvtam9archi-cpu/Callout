using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Callout.Logic;

namespace Callout.Tests
{
    public static class CalloutLeaderRouterTests
    {
        public static string Run()
        {
            var source = Rectangle(0, 0, 10, 10);
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(30, 0, 40, 10)).Count == 2, "horizontal straight route");
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(0, 30, 10, 40)).Count == 2, "vertical straight route");
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(30, 8, 40, 18)).Count == 2, "straight route through shared vertical interval");
            Validate(source, Rectangle(25, 25, 45, 45));
            Validate(source, Rectangle(-40, -30, -20, -10));
            Validate(source, Rectangle(8, 30, 18, 40));
            Validate(source, Rectangle(10.01, 5, 20.01, 15));
            Validate(source, Rectangle(-100, 30, -80, 50));
            var concave = new[] { new ClipPoint(0, 0), new ClipPoint(12, 0), new ClipPoint(12, 4),
                new ClipPoint(4, 4), new ClipPoint(4, 12), new ClipPoint(0, 12) };
            Validate(concave, Rectangle(20, 20, 35, 35));
            var circle = Enumerable.Range(0, 72).Select(i => new ClipPoint(10 * Math.Cos(i * Math.PI / 36), 10 * Math.Sin(i * Math.PI / 36))).ToArray();
            var otherCircle = circle.Select(p => new ClipPoint(p.X * 2 + 45, p.Y * 2 + 40)).ToArray();
            Validate(circle, otherCircle);
            Validate(circle.Reverse().ToArray(), otherCircle.Reverse().ToArray());
            Validate(Rectangle(1e9, 1e9, 1e9 + 10, 1e9 + 10), Rectangle(1e9 + 30, 1e9 + 30, 1e9 + 50, 1e9 + 50));
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(5, 5, 15, 15)).Count == 0, "overlap suppressed");
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(10, 0, 20, 10)).Count == 0, "touching frames suppressed");
            Check(CalloutLeaderRouter.CreateRoute(source, source).Count == 0, "coincident frames suppressed");
            Check(CalloutLeaderRouter.CreateRoute(source, Rectangle(2, 2, 8, 8)).Count == 0, "contained frame suppressed");

            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) CalloutLeaderRouter.CreateRoute(circle, otherCircle);
            watch.Stop();
            return "Passed 16 leader scenarios; circular route average " + (watch.Elapsed.TotalMilliseconds / 50).ToString("0.00") + " ms.";
        }

        private static void Validate(IReadOnlyList<ClipPoint> source, IReadOnlyList<ClipPoint> detail)
        {
            var route = CalloutLeaderRouter.CreateRoute(source, detail);
            Check(route.Count >= 2 && route.Count <= 4, "nonempty route with at most two bends");
            Check(OnBoundary(route[0], source) && OnBoundary(route[route.Count - 1], detail), "endpoints on actual boundaries");
            var polygons = new[] { new CalloutClipPolygon(source), new CalloutClipPolygon(detail) };
            for (int i = 1; i < route.Count; i++)
            {
                ClipPoint a = route[i - 1], b = route[i];
                Check(Math.Abs(a.X - b.X) < 1e-6 || Math.Abs(a.Y - b.Y) < 1e-6, "orthogonal segment");
                Check(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) > 1e-6, "no zero-length segment");
                foreach (var polygon in polygons)
                {
                    var cuts = polygon.GetBoundaryParameters(a, b);
                    cuts.Add(0); cuts.Add(1); cuts.Sort();
                    for (int j = 1; j < cuts.Count; j++)
                    {
                        if (cuts[j] - cuts[j - 1] < 1e-9) continue;
                        double t = (cuts[j] + cuts[j - 1]) / 2;
                        Check(!polygon.Contains(new ClipPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)), "no frame penetration");
                    }
                }
            }
        }

        private static bool OnBoundary(ClipPoint p, IReadOnlyList<ClipPoint> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                ClipPoint a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (Math.Abs((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / length < 1e-5 &&
                    p.X >= Math.Min(a.X, b.X) - 1e-5 && p.X <= Math.Max(a.X, b.X) + 1e-5 &&
                    p.Y >= Math.Min(a.Y, b.Y) - 1e-5 && p.Y <= Math.Max(a.Y, b.Y) + 1e-5) return true;
            }
            return false;
        }

        private static ClipPoint[] Rectangle(double x1, double y1, double x2, double y2) => new[]
        { new ClipPoint(x1, y1), new ClipPoint(x2, y1), new ClipPoint(x2, y2), new ClipPoint(x1, y2) };

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAILED leader: " + message);
        }
    }
}
