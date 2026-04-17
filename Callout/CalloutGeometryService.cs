using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Callout.Services
{
	public static class CalloutGeometryService
	{
		public static Polyline CreateSmartLeader(Extents3d origExt, Matrix3d mathTransform)
		{
			Point3d[] origMids = GetMidPoints(origExt);
			Point3d[] cloneMids = new Point3d[4];

			for (int i = 0; i < 4; i++) cloneMids[i] = origMids[i].TransformBy(mathTransform);

			Point3d bestStart = origMids[0];
			Point3d bestEnd = cloneMids[0];
			double minDist = double.MaxValue;

			foreach (var startPt in origMids)
			{
				foreach (var endPt in cloneMids)
				{
					double dist = startPt.DistanceTo(endPt);
					if (dist < minDist)
					{
						minDist = dist;
						bestStart = startPt;
						bestEnd = endPt;
					}
				}
			}

			return CreateOrthogonalLeader(bestStart, bestEnd);
		}

		private static Point3d[] GetMidPoints(Extents3d ext)
		{
			Point3d min = ext.MinPoint;
			Point3d max = ext.MaxPoint;
			return new Point3d[]
			{
				new Point3d(min.X, (min.Y + max.Y) / 2.0, min.Z),
				new Point3d(max.X, (min.Y + max.Y) / 2.0, min.Z),
				new Point3d((min.X + max.X) / 2.0, max.Y, min.Z),
				new Point3d((min.X + max.X) / 2.0, min.Y, min.Z)
			};
		}

		private static Polyline CreateOrthogonalLeader(Point3d startPt, Point3d endPt)
		{
			Polyline pl = new Polyline();
			pl.SetDatabaseDefaults();

			pl.AddVertexAt(0, new Point2d(startPt.X, startPt.Y), 0, 0, 0);

			if (System.Math.Abs(endPt.X - startPt.X) > System.Math.Abs(endPt.Y - startPt.Y))
			{
				double midX = (startPt.X + endPt.X) / 2.0;
				pl.AddVertexAt(1, new Point2d(midX, startPt.Y), 0, 0, 0);
				pl.AddVertexAt(2, new Point2d(midX, endPt.Y), 0, 0, 0);
			}
			else
			{
				double midY = (startPt.Y + endPt.Y) / 2.0;
				pl.AddVertexAt(1, new Point2d(startPt.X, midY), 0, 0, 0);
				pl.AddVertexAt(2, new Point2d(endPt.X, midY), 0, 0, 0);
			}

			pl.AddVertexAt(3, new Point2d(endPt.X, endPt.Y), 0, 0, 0);
			return pl;
		}
	}
}