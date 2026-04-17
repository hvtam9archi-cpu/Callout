using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Callout.Jigs;
using Callout.Services;

[assembly: CommandClass(typeof(Callout.Commands.CalloutCommand))]

namespace Callout.Commands
{
	public class CalloutCommand
	{
		[CommandMethod("CT")]
		public void CreateCallout()
		{
			Document doc = Application.DocumentManager.MdiActiveDocument;
			Database db = doc.Database;
			Editor ed = doc.Editor;

			using (DocumentLock docLock = doc.LockDocument())
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				try
				{
					// 1. Nhập liệu
					var optBlk = new PromptEntityOptions("\nChọn Block cần trích: ");
					optBlk.SetRejectMessage("\nChỉ chọn BlockReference!");
					optBlk.AddAllowedClass(typeof(BlockReference), true);
					var resBlk = ed.GetEntity(optBlk);
					if (resBlk.Status != PromptStatus.OK) return;

					var optPl = new PromptEntityOptions("\nChọn Polyline làm khung: ");
					optPl.SetRejectMessage("\nChỉ chọn Polyline!");
					optPl.AddAllowedClass(typeof(Polyline), true);
					var resPl = ed.GetEntity(optPl);
					if (resPl.Status != PromptStatus.OK) return;

					var sourceBlock = tr.GetObject(resBlk.ObjectId, OpenMode.ForRead) as BlockReference;
					var boundaryPoly = tr.GetObject(resPl.ObjectId, OpenMode.ForRead) as Polyline;

					Extents3d origExt = boundaryPoly.GeometricExtents;
					Point3d basePoint = new Point3d(
						(origExt.MinPoint.X + origExt.MaxPoint.X) / 2.0,
						(origExt.MinPoint.Y + origExt.MaxPoint.Y) / 2.0,
						(origExt.MinPoint.Z + origExt.MaxPoint.Z) / 2.0
					);

					// 2. Kỹ thuật Đỉnh cao: Gói chi tiết vào một Anonymous Block (*U)
					BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
					BlockTableRecord btrAnon = new BlockTableRecord
					{
						Name = "*U" // Báo cho AutoCAD đây là Block ẩn
					};
					ObjectId anonId = bt.Add(btrAnon);
					tr.AddNewlyCreatedDBObject(btrAnon, true);

					// Clone và dời gốc tọa độ về tâm của block ẩn
					BlockReference innerBlock = sourceBlock.Clone() as BlockReference;
					Polyline innerPoly = boundaryPoly.Clone() as Polyline;
					Vector3d toOrigin = basePoint.GetVectorTo(Point3d.Origin);

					innerBlock.TransformBy(Matrix3d.Displacement(toOrigin));
					innerPoly.TransformBy(Matrix3d.Displacement(toOrigin));

					btrAnon.AppendEntity(innerBlock);
					tr.AddNewlyCreatedDBObject(innerBlock, true);
					btrAnon.AppendEntity(innerPoly);
					tr.AddNewlyCreatedDBObject(innerPoly, true);

					// Áp dụng XClip vào thành phần bên trong (XClip sẽ hoạt động hoàn hảo)
					ApplyXClip(tr, innerBlock, innerPoly);

					// Tạo đối tượng tham chiếu kéo thả trên RAM
					BlockReference jigRef = new BlockReference(basePoint, anonId);

					// 3. Chạy Jig
					JigInputHandler.Start();
					var calloutJig = new CalloutJig(jigRef, basePoint, origExt);
					PromptResult jigRes = ed.Drag(calloutJig);
					JigInputHandler.Stop();

					// 4. Kết thúc lệnh
					if (jigRes.Status == PromptStatus.OK)
					{
						BlockTableRecord cSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

						// Thêm Block trích xuất
						jigRef.Position = calloutJig.CurrentPosition;
						jigRef.ScaleFactors = new Scale3d(JigInputHandler.CurrentScale);
						cSpace.AppendEntity(jigRef);
						tr.AddNewlyCreatedDBObject(jigRef, true);

						// Thêm đường Leader nối
						Polyline finalLeader = CalloutGeometryService.CreateSmartLeader(origExt, calloutJig.MathTransform);
						cSpace.AppendEntity(finalLeader);
						tr.AddNewlyCreatedDBObject(finalLeader, true);

						tr.Commit();
					}
					else
					{
						tr.Abort();
					}
				}
				catch (System.Exception ex)
				{
					ed.WriteMessage($"\nLỗi: {ex.Message}");
					tr.Abort();
				}
			}
		}

		private void ApplyXClip(Transaction tr, BlockReference br, Polyline pl)
		{
			br.CreateExtensionDictionary();
			DBDictionary extDict = (DBDictionary)tr.GetObject(br.ExtensionDictionary, OpenMode.ForWrite);

			DBDictionary filterDict = new DBDictionary();
			extDict.SetAt("ACAD_FILTER", filterDict);
			tr.AddNewlyCreatedDBObject(filterDict, true);

			Matrix3d wcsToBlk = br.BlockTransform.Inverse();
			Point2dCollection pts = new Point2dCollection();
			for (int i = 0; i < pl.NumberOfVertices; i++)
			{
				Point3d ptBlk = pl.GetPoint3dAt(i).TransformBy(wcsToBlk);
				pts.Add(new Point2d(ptBlk.X, ptBlk.Y));
			}

			SpatialFilterDefinition sfd = new SpatialFilterDefinition(pts, Vector3d.ZAxis, 0.0, 0.0, 0.0, true);
			SpatialFilter sf = new SpatialFilter() { Definition = sfd };

			filterDict.SetAt("SPATIAL", sf);
			tr.AddNewlyCreatedDBObject(sf, true);
		}
	}
}