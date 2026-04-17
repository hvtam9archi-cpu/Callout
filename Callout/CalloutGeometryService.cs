using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Callout.Services
{
    public static class CalloutGeometryService
    {
        public static IEnumerable<Entity> CreateSmartLeader(Extents3d originalExtents, Matrix3d mathTransform, double dotDiameter = 0.0)
        {
            Point3d[] originalMidPoints = GetRectangleMidPoints(originalExtents);
            Point3d[] clonedMidPoints = new Point3d[4];

            for (int i = 0; i < 4; i++)
            {
                clonedMidPoints[i] = originalMidPoints[i].TransformBy(mathTransform);
            }

            Point3d bestStartNode = originalMidPoints[0];
            Point3d bestEndNode = clonedMidPoints[0];
            double minimumDistance = double.MaxValue;

            foreach (var startPoint in originalMidPoints)
            {
                foreach (var endPoint in clonedMidPoints)
                {
                    double currentDistance = startPoint.DistanceTo(endPoint);
                    if (currentDistance < minimumDistance)
                    {
                        minimumDistance = currentDistance;
                        bestStartNode = startPoint;
                        bestEndNode = endPoint;
                    }
                }
            }

            var leader = CreateOrthogonalLeader(bestStartNode, bestEndNode);
            var entities = new List<Entity> { leader };

            if (dotDiameter > 0)
            {
                // Create Filled Dots (Donut with inner radius 0)
                entities.Add(CreateDot(bestStartNode, dotDiameter));
                entities.Add(CreateDot(bestEndNode, dotDiameter));
            }

            return entities;
        }

        private static Entity CreateDot(Point3d center, double diameter)
        {
            Polyline dot = new Polyline();
            dot.SetDatabaseDefaults();
            double radius = diameter / 2.0;

            // Donut technique: two semi-circles with width = diameter, path radius = radius/2
            dot.AddVertexAt(0, new Point2d(center.X - radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.AddVertexAt(1, new Point2d(center.X + radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.Closed = true;

            return dot;
        }

        private static Point3d[] GetRectangleMidPoints(Extents3d extents)
        {
            Point3d minimumPoint = extents.MinPoint;
            Point3d maximumPoint = extents.MaxPoint;

            return new Point3d[]
            {
                new Point3d(minimumPoint.X, (minimumPoint.Y + maximumPoint.Y) / 2.0, minimumPoint.Z),
                new Point3d(maximumPoint.X, (minimumPoint.Y + maximumPoint.Y) / 2.0, minimumPoint.Z),
                new Point3d((minimumPoint.X + maximumPoint.X) / 2.0, maximumPoint.Y, minimumPoint.Z),
                new Point3d((minimumPoint.X + maximumPoint.X) / 2.0, minimumPoint.Y, minimumPoint.Z)
            };
        }

        private static Polyline CreateOrthogonalLeader(Point3d startPoint, Point3d endPoint)
        {
            Polyline routePolyline = new Polyline();
            routePolyline.SetDatabaseDefaults();

            routePolyline.AddVertexAt(0, new Point2d(startPoint.X, startPoint.Y), 0, 0, 0);

            double dx = System.Math.Abs(endPoint.X - startPoint.X);
            double dy = System.Math.Abs(endPoint.Y - startPoint.Y);

            // Xử lý tạo 1 đoạn thẳng duy nhất nếu đã thẳng hàng (sai số < 0.001)
            if (dx < 0.001 || dy < 0.001)
            {
                routePolyline.AddVertexAt(1, new Point2d(endPoint.X, endPoint.Y), 0, 0, 0);
                return routePolyline;
            }

            if (dx > dy)
            {
                double middleX = (startPoint.X + endPoint.X) / 2.0;
                routePolyline.AddVertexAt(1, new Point2d(middleX, startPoint.Y), 0, 0, 0);
                routePolyline.AddVertexAt(2, new Point2d(middleX, endPoint.Y), 0, 0, 0);
            }
            else
            {
                double middleY = (startPoint.Y + endPoint.Y) / 2.0;
                routePolyline.AddVertexAt(1, new Point2d(startPoint.X, middleY), 0, 0, 0);
                routePolyline.AddVertexAt(2, new Point2d(endPoint.X, middleY), 0, 0, 0);
            }

            routePolyline.AddVertexAt(3, new Point2d(endPoint.X, endPoint.Y), 0, 0, 0);
            return routePolyline;
        }
    }
}
