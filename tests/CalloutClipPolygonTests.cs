using System;
using System.Collections.Generic;
using System.Linq;
using Callout.Logic;

namespace Callout.Tests
{
    public static class CalloutClipPolygonTests
    {
        private static int _assertions;

        public static string Run()
        {
            _assertions = 0;
            var square = Polygon(0, 0, 4, 0, 4, 4, 0, 4);
            Check(square.Contains(new ClipPoint(2, 2)), "inside square");
            Check(square.Contains(new ClipPoint(0, 2)), "boundary included");
            Check(!square.Contains(new ClipPoint(5, 2)), "outside square");

            var concave = Polygon(0, 0, 6, 0, 6, 6, 4, 6, 4, 2, 2, 2, 2, 6, 0, 6);
            Check(!concave.Contains(new ClipPoint(3, 4)), "concave cutout excluded");
            Check(concave.Contains(new ClipPoint(1, 4)), "concave arm included");
            var concaveHits = concave.GetVisibleParameters(new ClipPoint(-1, 4), new ClipPoint(7, 4));
            foreach (double expected in new[] { 0.125, 0.375, 0.625, 0.875 })
                Check(concaveHits.Any(value => Near(value, expected)), "concave boundary crossings");

            var circle = new CalloutClipPolygon(Enumerable.Range(0, 72)
                .Select(i => new ClipPoint(10 * Math.Cos(i * Math.PI / 36), 10 * Math.Sin(i * Math.PI / 36))));
            Check(!circle.Contains(new ClipPoint(9, 9)), "circle bounding-box corner excluded");
            Check(circle.Contains(new ClipPoint(5, 5)), "circle interior included");

            var hits = square.GetVisibleParameters(new ClipPoint(-1, 2), new ClipPoint(5, 2));
            Check(hits.Count == 2 && Near(hits[0], 1.0 / 6) && Near(hits[1], 5.0 / 6), "both endpoints outside, crossing visible");
            var overlap = square.GetVisibleParameters(new ClipPoint(-1, 0), new ClipPoint(5, 0));
            Check(overlap.Any(t => Near(t, 1.0 / 6)) && overlap.Any(t => Near(t, 5.0 / 6)), "collinear boundary overlap");
            Check(square.GetVisibleParameters(new ClipPoint(-1, -1), new ClipPoint(0, 0)).Any(t => Near(t, 1)), "vertex tangent");
            Check(square.GetVisibleParameters(new ClipPoint(2, 2), new ClipPoint(2, 2)).Count > 0, "zero-length inside");
            Check(square.GetVisibleParameters(new ClipPoint(5, 5), new ClipPoint(5, 5)).Count == 0, "zero-length outside");

            Reject(() => Polygon(0, 0, 4, 4, 0, 5, 5, 0), "self-intersection with nonzero signed area");
            Reject(() => Polygon(0, 0, 4, 0, 2, 0, 4, 4, 0, 4), "adjacent overlapping edges");
            Reject(() => Polygon(0, 0, 4, 0, 4, 0, 0, 4), "duplicate adjacent vertex");
            Reject(() => Polygon(0, 0, 4, 0, 4, 4, 0, 0, 0, 4), "repeated nonadjacent vertex");
            Reject(() => Polygon(0, 0, 2, 0, 4, 0), "zero area");
            Reject(() => Polygon(0, 0, 4, 0, double.NaN, 4), "non-finite coordinate");
            Check(Polygon(0, 0, 0, 4, 4, 4, 4, 0).Contains(new ClipPoint(2, 2)), "clockwise winding");
            Check(Polygon(0, 0, 4, 0, 4, 4, 0, 4, 0, 0).Contains(new ClipPoint(2, 2)), "explicit closing vertex");
            Check(Polygon(0, 0, 2, 0, 4, 0, 4, 4, 0, 4).Contains(new ClipPoint(2, 2)), "non-overlapping collinear edges");
            Check(Polygon(1e9, 1e9, 1e9 + 4, 1e9, 1e9 + 4, 1e9 + 4, 1e9, 1e9 + 4)
                .Contains(new ClipPoint(1e9 + 2, 1e9 + 2)), "large WCS coordinates");
            return "Passed " + _assertions + " geometry assertions.";
        }

        private static CalloutClipPolygon Polygon(params double[] coordinates)
        {
            var points = new List<ClipPoint>();
            for (int i = 0; i < coordinates.Length; i += 2)
                points.Add(new ClipPoint(coordinates[i], coordinates[i + 1]));
            return new CalloutClipPolygon(points);
        }

        private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-8;
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            _assertions++;
        }

        private static void Reject(Action action, string name)
        {
            try { action(); }
            catch (InvalidOperationException) { _assertions++; return; }
            throw new InvalidOperationException("FAILED: accepted " + name);
        }
    }
}
