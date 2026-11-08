using System;
using System.Collections.Generic;

namespace Callout.Logic
{
    internal readonly struct ClipPoint
    {
        public double X { get; }
        public double Y { get; }

        public ClipPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    // Pure XY geometry shared by clipping validation and dimension extraction.
    internal sealed class CalloutClipPolygon
    {
        private const double Tolerance = 1.0e-6;
        private readonly List<ClipPoint> _vertices;

        public CalloutClipPolygon(IEnumerable<ClipPoint> vertices)
        {
            _vertices = new List<ClipPoint>(vertices);
            if (_vertices.Count > 1 && Distance(_vertices[0], _vertices[_vertices.Count - 1]) <= Tolerance)
                _vertices.RemoveAt(_vertices.Count - 1);

            if (_vertices.Count < 3)
                throw new InvalidOperationException("Khung trích phải có ít nhất ba đỉnh phân biệt.");

            double twiceArea = 0.0;
            ClipPoint origin = _vertices[0];
            for (int i = 0; i < _vertices.Count; i++)
            {
                ClipPoint a = _vertices[i];
                ClipPoint b = _vertices[(i + 1) % _vertices.Count];
                if (!IsFinite(a.X) || !IsFinite(a.Y) || Distance(a, b) <= Tolerance)
                    throw new InvalidOperationException("Khung trích chứa tọa độ không hợp lệ hoặc đỉnh trùng nhau.");

                twiceArea += Cross(a.X - origin.X, a.Y - origin.Y, b.X - origin.X, b.Y - origin.Y);
                ClipPoint previous = _vertices[(i + _vertices.Count - 1) % _vertices.Count];
                if (OnSegment(previous, a, b) || OnSegment(b, previous, a))
                    throw new InvalidOperationException("Khung trích có các cạnh chồng lên nhau.");
            }

            if (Math.Abs(twiceArea) <= Tolerance * Tolerance)
                throw new InvalidOperationException("Khung trích phải có diện tích khác không.");

            var intersections = new List<double>();
            for (int i = 0; i < _vertices.Count; i++)
            {
                for (int j = i + 1; j < _vertices.Count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == _vertices.Count - 1)) continue;
                    intersections.Clear();
                    AppendIntersections(_vertices[i], _vertices[(i + 1) % _vertices.Count],
                        _vertices[j], _vertices[(j + 1) % _vertices.Count], intersections);
                    if (intersections.Count > 0)
                        throw new InvalidOperationException("Khung trích tự giao nhau; hãy chọn một đường bao đơn.");
                }
            }
        }

        public bool Contains(ClipPoint point)
        {
            bool inside = false;
            for (int i = 0; i < _vertices.Count; i++)
            {
                ClipPoint a = _vertices[i];
                ClipPoint b = _vertices[(i + 1) % _vertices.Count];
                if (OnSegment(point, a, b)) return true;
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    inside = !inside;
            }
            return inside;
        }

        // Returns visible endpoints and boundary crossings, including collinear overlap.
        public List<double> GetVisibleParameters(ClipPoint start, ClipPoint end)
        {
            List<double> parameters = GetBoundaryParameters(start, end);
            if (Contains(start)) parameters.Add(0.0);
            if (Contains(end)) parameters.Add(1.0);
            parameters.Sort();
            return parameters;
        }

        public List<double> GetBoundaryParameters(ClipPoint start, ClipPoint end)
        {
            List<double> parameters = new List<double>();
            for (int i = 0; i < _vertices.Count; i++)
                AppendIntersections(start, end, _vertices[i], _vertices[(i + 1) % _vertices.Count], parameters);
            parameters.Sort();
            return parameters;
        }

        private static void AppendIntersections(ClipPoint a, ClipPoint b, ClipPoint c, ClipPoint d, List<double> result)
        {
            double rx = b.X - a.X, ry = b.Y - a.Y;
            double sx = d.X - c.X, sy = d.Y - c.Y;
            double length = Distance(a, b), otherLength = Distance(c, d);
            if (length <= Tolerance)
            {
                if (OnSegment(a, c, d)) result.Add(0.0);
                return;
            }

            double denominator = Cross(rx, ry, sx, sy);
            if (Math.Abs(denominator) <= Tolerance * Math.Max(length, otherLength))
            {
                if (OnSegment(c, a, b)) result.Add(Project(c, a, rx, ry));
                if (OnSegment(d, a, b)) result.Add(Project(d, a, rx, ry));
                if (OnSegment(a, c, d)) result.Add(0.0);
                if (OnSegment(b, c, d)) result.Add(1.0);
                return;
            }

            double t = Cross(c.X - a.X, c.Y - a.Y, sx, sy) / denominator;
            double u = Cross(c.X - a.X, c.Y - a.Y, rx, ry) / denominator;
            if (t >= -Tolerance / length && t <= 1.0 + Tolerance / length &&
                u >= -Tolerance / otherLength && u <= 1.0 + Tolerance / otherLength)
                result.Add(Math.Max(0.0, Math.Min(1.0, t)));
        }

        private static bool OnSegment(ClipPoint point, ClipPoint a, ClipPoint b)
        {
            double length = Distance(a, b);
            if (length <= Tolerance) return Distance(point, a) <= Tolerance;
            return Math.Abs(Cross(b.X - a.X, b.Y - a.Y, point.X - a.X, point.Y - a.Y)) <= Tolerance * length &&
                point.X >= Math.Min(a.X, b.X) - Tolerance && point.X <= Math.Max(a.X, b.X) + Tolerance &&
                point.Y >= Math.Min(a.Y, b.Y) - Tolerance && point.Y <= Math.Max(a.Y, b.Y) + Tolerance;
        }

        private static double Project(ClipPoint p, ClipPoint a, double x, double y)
        {
            return Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * x + (p.Y - a.Y) * y) / (x * x + y * y)));
        }

        private static double Cross(double x, double y, double otherX, double otherY) => x * otherY - y * otherX;
        private static double Distance(ClipPoint a, ClipPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
