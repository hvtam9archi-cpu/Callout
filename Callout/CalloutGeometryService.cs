using System;
using System.Collections.Generic;
using Callout.Logic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Callout.Services
{
    public static class CalloutGeometryService
    {
        // Inputs and returned entities are in drawing WCS. The caller owns all
        // returned entities until they are appended to a transaction.
        public static List<Entity> CreateSmartLeader(
            IReadOnlyList<Point3d> sourceBoundary, Matrix3d detailTransform, double dotDiameter = 0.0, Database database = null)
        {
            var source = new List<ClipPoint>();
            var detail = new List<ClipPoint>();
            foreach (Point3d point in sourceBoundary)
            {
                Point3d transformed = point.TransformBy(detailTransform);
                source.Add(new ClipPoint(point.X, point.Y));
                detail.Add(new ClipPoint(transformed.X, transformed.Y));
            }
            List<ClipPoint> route = CalloutLeaderRouter.CreateRoute(source, detail);
            var entities = new List<Entity>();
            if (route.Count < 2) return entities;

            try
            {
                var leader = new Polyline(route.Count);
                entities.Add(leader);
                if (database == null) leader.SetDatabaseDefaults();
                else leader.SetDatabaseDefaults(database);
                leader.Elevation = sourceBoundary[0].Z;
                for (int i = 0; i < route.Count; i++)
                    leader.AddVertexAt(i, new Point2d(route[i].X, route[i].Y), 0, 0, 0);
                if (dotDiameter > 0)
                {
                    entities.Add(CalloutHelpers.CreateLeaderDot(new Point3d(route[0].X, route[0].Y, leader.Elevation), dotDiameter, database));
                    ClipPoint end = route[route.Count - 1];
                    entities.Add(CalloutHelpers.CreateLeaderDot(new Point3d(end.X, end.Y, leader.Elevation), dotDiameter, database));
                }
                return entities;
            }
            catch (Exception)
            {
                foreach (Entity entity in entities) entity.Dispose();
                throw;
            }
        }
    }
}
