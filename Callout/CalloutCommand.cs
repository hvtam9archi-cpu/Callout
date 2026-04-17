using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
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
					var optBlk = new PromptEntityOptions("\nChọn Block cần trích: ");
					optBlk.SetRejectMessage("\nChỉ chọn BlockReference!");
					optBlk.AddAllowedClass(typeof(BlockReference), true);
					var resBlk = ed.GetEntity(optBlk);
					if (resBlk.Status != PromptStatus.OK) return;

					var optPl = new PromptEntityOptions("\nChọn Polyline làm khung trích: ");
					optPl.SetRejectMessage("\nChỉ chọn Polyline!");
					optPl.AddAllowedClass(typeof(Polyline), true);
					var resPl = ed.GetEntity(optPl);
					if (resPl.Status != PromptStatus.OK) return;

					var sourceBlock = tr.GetObject(resBlk.ObjectId, OpenMode.ForRead) as BlockReference;
					var boundaryPoly = tr.GetObject(resPl.ObjectId, OpenMode.ForRead) as Polyline;

					ObjectId sourceLayerId = boundaryPoly.LayerId;

					Extents3d origExt = boundaryPoly.GeometricExtents;
					Point3d basePoint = new Point3d(
						(origExt.MinPoint.X + origExt.MaxPoint.X) / 2.0,
						(origExt.MinPoint.Y + origExt.MaxPoint.Y) / 2.0,
						(origExt.MinPoint.Z + origExt.MaxPoint.Z) / 2.0
					);

					ObjectId layerNenId = EnsureLayer(db, tr, "Nen", Color.FromRgb(214, 214, 214), "DASHED2", LineWeight.LineWeight005);
					ObjectId layerDimId = EnsureLayer(db, tr, "ABC_A_Kichthuoc", Color.FromColorIndex(ColorMethod.ByAci, 8), "Continuous", LineWeight.LineWeight009);

					BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
					BlockTableRecord btrAnon = new BlockTableRecord { Name = "*U" };
					ObjectId anonId = bt.Add(btrAnon);
					tr.AddNewlyCreatedDBObject(btrAnon, true);

					BlockReference innerBlock = sourceBlock.Clone() as BlockReference;
					Polyline innerPoly = boundaryPoly.Clone() as Polyline;

					innerPoly.LayerId = sourceLayerId;

					Vector3d toOrigin = basePoint.GetVectorTo(Point3d.Origin);
					innerBlock.TransformBy(Matrix3d.Displacement(toOrigin));
					innerPoly.TransformBy(Matrix3d.Displacement(toOrigin));

					btrAnon.AppendEntity(innerBlock);
					tr.AddNewlyCreatedDBObject(innerBlock, true);
					btrAnon.AppendEntity(innerPoly);
					tr.AddNewlyCreatedDBObject(innerPoly, true);

					ApplyXClip(tr, innerBlock, innerPoly);

					BlockReference jigRef = new BlockReference(basePoint, anonId) { LayerId = sourceLayerId };

					JigInputHandler.Start();
					var calloutJig = new CalloutJig(jigRef, basePoint, origExt);
					PromptResult jigRes = ed.Drag(calloutJig);
					JigInputHandler.Stop();

					if (jigRes.Status == PromptStatus.OK)
					{
						double calloutScale = JigInputHandler.CurrentScale;

						PromptDoubleOptions optDimScale = new PromptDoubleOptions("\nNhập tỷ lệ Dimscale cho chi tiết trích: ")
						{
							AllowZero = false,
							AllowNegative = false,
							DefaultValue = calloutScale,
							UseDefaultValue = true
						};

						PromptDoubleResult resDimScale = ed.GetDouble(optDimScale);
						if (resDimScale.Status != PromptStatus.OK) { tr.Abort(); return; }

						double dimScale = resDimScale.Value;
						BlockTableRecord cSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

						Point3d finalPos = calloutJig.CurrentPosition;
						jigRef.Position = finalPos;
						jigRef.ScaleFactors = new Scale3d(calloutScale);
						cSpace.AppendEntity(jigRef);
						tr.AddNewlyCreatedDBObject(jigRef, true);

						Polyline finalLeader = CalloutGeometryService.CreateSmartLeader(origExt, calloutJig.MathTransform);
						finalLeader.LayerId = sourceLayerId;
						cSpace.AppendEntity(finalLeader);
						tr.AddNewlyCreatedDBObject(finalLeader, true);

						string styleName = $"TB_ABC_DIM 1-{dimScale:0.##}";
						ObjectId dimStyleId = CreateOrUpdateCalloutDimStyle(db, tr, dimScale, styleName);

						AutoQDimInnerGeometry(sourceBlock, boundaryPoly, calloutJig.MathTransform, cSpace, dimStyleId, dimScale, calloutScale, basePoint, finalPos, layerNenId, layerDimId, tr);

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

		private void ExtractGeometryRecursive(ObjectId btrId, Matrix3d matrix, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPts, List<Arc> validArcs, Transaction tr)
		{
			BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
			foreach (ObjectId entId in btr)
			{
				Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
				if (ent == null) continue;

				if (ent is BlockReference blkRef)
				{
					Matrix3d newMatrix = matrix * blkRef.BlockTransform;
					ExtractGeometryRecursive(blkRef.BlockTableRecord, newMatrix, clipBox, finalTransform, validPts, validArcs, tr);
				}
				else if (ent is Line line)
				{
					AddPointIfInsideClip(line.StartPoint.TransformBy(matrix), clipBox, finalTransform, validPts);
					AddPointIfInsideClip(line.EndPoint.TransformBy(matrix), clipBox, finalTransform, validPts);
				}
				else if (ent is Polyline pl)
				{
					for (int i = 0; i < pl.NumberOfVertices; i++)
					{
						AddPointIfInsideClip(pl.GetPoint3dAt(i).TransformBy(matrix), clipBox, finalTransform, validPts);
					}
				}
				else if (ent is Arc arc)
				{
					Point3d midPtWcs = arc.GetPointAtParameter(arc.StartParam + (arc.EndParam - arc.StartParam) / 2.0).TransformBy(matrix);
					if (IsInsideClip(midPtWcs, clipBox))
					{
						Arc clonedArc = arc.Clone() as Arc;
						clonedArc.TransformBy(matrix * finalTransform);
						validArcs.Add(clonedArc);
					}
				}
			}
		}

		private void AutoQDimInnerGeometry(BlockReference sourceBlock, Polyline boundaryPoly, Matrix3d finalTransform, BlockTableRecord cSpace, ObjectId dimStyleId, double dimScale, double calloutScale, Point3d origCenter, Point3d newCenter, ObjectId layerNenId, ObjectId layerDimId, Transaction tr)
		{
			List<Point3d> validPoints = new List<Point3d>();
			List<Arc> validArcs = new List<Arc>();

			Extents3d clipBox = boundaryPoly.GeometricExtents;

			ExtractGeometryRecursive(sourceBlock.BlockTableRecord, sourceBlock.BlockTransform, clipBox, finalTransform, validPoints, validArcs, tr);

			Extents3d finalBoundaryExt;
			using (Polyline cloneBound = boundaryPoly.Clone() as Polyline)
			{
				cloneBound.TransformBy(finalTransform);
				finalBoundaryExt = cloneBound.GeometricExtents;
			}

			bool placeTop = newCenter.Y >= origCenter.Y;
			double refY = placeTop ? finalBoundaryExt.MaxPoint.Y : finalBoundaryExt.MinPoint.Y;
			double dirY = placeTop ? 1.0 : -1.0;

			bool placeRight = newCenter.X >= origCenter.X;
			double refX = placeRight ? finalBoundaryExt.MaxPoint.X : finalBoundaryExt.MinPoint.X;
			double dirX = placeRight ? 1.0 : -1.0;

			double firstLayerOffset = 8.0 * dimScale;
			double secondLayerOffset = firstLayerOffset + (6.0 * dimScale);
			double dimLinearFactor = 1.0 / calloutScale;

			foreach (Arc vArc in validArcs)
			{
				Point3d midPt = vArc.GetPointAtParameter(vArc.StartParam + (vArc.EndParam - vArc.StartParam) / 2.0);
				Vector3d dirToMid = vArc.Center.GetVectorTo(midPt).GetNormal();
				Point3d targetArcPoint = vArc.Center + dirToMid * (vArc.Radius + firstLayerOffset);

				ArcDimension arcDim = new ArcDimension(
					vArc.Center, vArc.StartPoint, vArc.EndPoint, targetArcPoint, "", dimStyleId)
				{
					LayerId = layerDimId
				};

				arcDim.SetDatabaseDefaults();
				arcDim.Dimlfac = dimLinearFactor;
				SetFixedExtensionLine(arcDim, 6.0);

				cSpace.AppendEntity(arcDim);
				tr.AddNewlyCreatedDBObject(arcDim, true);

				vArc.Dispose();
			}

			if (validPoints.Count == 0) return;

			var distinctX = validPoints.Select(p => Math.Round(p.X, 2)).Distinct().OrderBy(x => x).ToList();
			var distinctY = validPoints.Select(p => Math.Round(p.Y, 2)).Distinct().OrderBy(y => y).ToList();

			if (distinctX.Count > 1)
			{
				foreach (double x in distinctX)
				{
					Line guide = new Line(new Point3d(x, finalBoundaryExt.MinPoint.Y, 0), new Point3d(x, finalBoundaryExt.MaxPoint.Y, 0))
					{
						LayerId = layerNenId
					};
					cSpace.AppendEntity(guide);
					tr.AddNewlyCreatedDBObject(guide, true);
				}

				for (int i = 0; i < distinctX.Count - 1; i++)
				{
					RotatedDimension dimX = new RotatedDimension() { LayerId = layerDimId };
					dimX.SetDatabaseDefaults();
					dimX.DimensionStyle = dimStyleId;
					dimX.Dimlfac = dimLinearFactor;
					SetFixedExtensionLine(dimX, 6.0);

					dimX.XLine1Point = new Point3d(distinctX[i], refY, 0);
					dimX.XLine2Point = new Point3d(distinctX[i + 1], refY, 0);
					dimX.DimLinePoint = new Point3d(distinctX[i], refY + (dirY * firstLayerOffset), 0);
					dimX.Rotation = 0.0;
					cSpace.AppendEntity(dimX);
					tr.AddNewlyCreatedDBObject(dimX, true);
				}

				if (distinctX.Count > 2)
				{
					RotatedDimension dimTotalX = new RotatedDimension() { LayerId = layerDimId };
					dimTotalX.SetDatabaseDefaults();
					dimTotalX.DimensionStyle = dimStyleId;
					dimTotalX.Dimlfac = dimLinearFactor;
					SetFixedExtensionLine(dimTotalX, 6.0);

					dimTotalX.XLine1Point = new Point3d(distinctX.First(), refY, 0);
					dimTotalX.XLine2Point = new Point3d(distinctX.Last(), refY, 0);
					dimTotalX.DimLinePoint = new Point3d(distinctX.First(), refY + (dirY * secondLayerOffset), 0);
					dimTotalX.Rotation = 0.0;
					cSpace.AppendEntity(dimTotalX);
					tr.AddNewlyCreatedDBObject(dimTotalX, true);
				}
			}

			if (distinctY.Count > 1)
			{
				foreach (double y in distinctY)
				{
					Line guide = new Line(new Point3d(finalBoundaryExt.MinPoint.X, y, 0), new Point3d(finalBoundaryExt.MaxPoint.X, y, 0))
					{
						LayerId = layerNenId
					};
					cSpace.AppendEntity(guide);
					tr.AddNewlyCreatedDBObject(guide, true);
				}

				for (int i = 0; i < distinctY.Count - 1; i++)
				{
					RotatedDimension dimY = new RotatedDimension() { LayerId = layerDimId };
					dimY.SetDatabaseDefaults();
					dimY.DimensionStyle = dimStyleId;
					dimY.Dimlfac = dimLinearFactor;
					SetFixedExtensionLine(dimY, 6.0);

					dimY.XLine1Point = new Point3d(refX, distinctY[i], 0);
					dimY.XLine2Point = new Point3d(refX, distinctY[i + 1], 0);
					dimY.DimLinePoint = new Point3d(refX + (dirX * firstLayerOffset), distinctY[i], 0);
					dimY.Rotation = Math.PI / 2.0;
					cSpace.AppendEntity(dimY);
					tr.AddNewlyCreatedDBObject(dimY, true);
				}

				if (distinctY.Count > 2)
				{
					RotatedDimension dimTotalY = new RotatedDimension() { LayerId = layerDimId };
					dimTotalY.SetDatabaseDefaults();
					dimTotalY.DimensionStyle = dimStyleId;
					dimTotalY.Dimlfac = dimLinearFactor;
					SetFixedExtensionLine(dimTotalY, 6.0);

					dimTotalY.XLine1Point = new Point3d(refX, distinctY.First(), 0);
					dimTotalY.XLine2Point = new Point3d(refX, distinctY.Last(), 0);
					dimTotalY.DimLinePoint = new Point3d(refX + (dirX * secondLayerOffset), distinctY.First(), 0);
					dimTotalY.Rotation = Math.PI / 2.0;
					cSpace.AppendEntity(dimTotalY);
					tr.AddNewlyCreatedDBObject(dimTotalY, true);
				}
			}
		}

		// ==========================================
		// CÁC HÀM HELPER ĐỘC LẬP
		// ==========================================
		private ObjectId EnsureLayer(Database db, Transaction tr, string layerName, Color color, string lineTypeName, LineWeight lw)
		{
			LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);

			// SỬA LỖI CS1061: Lấy Linetype Continuous an toàn chuẩn API AutoCAD
			ObjectId ltId = SymbolUtilityServices.GetLinetypeContinuousId(db);

			if (!string.IsNullOrEmpty(lineTypeName))
			{
				LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
				if (!ltt.Has(lineTypeName))
				{
					string filename = (short)Application.GetSystemVariable("MEASUREMENT") == 0 ? "acad.lin" : "acadiso.lin";
					try { db.LoadLineTypeFile(lineTypeName, filename); } catch { }
				}
				if (ltt.Has(lineTypeName)) ltId = ltt[lineTypeName];
			}

			LayerTableRecord ltr;
			if (lt.Has(layerName))
			{
				ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
			}
			else
			{
				ltr = new LayerTableRecord { Name = layerName };
				lt.Add(ltr);
				tr.AddNewlyCreatedDBObject(ltr, true);
			}

			ltr.Color = color;
			ltr.LinetypeObjectId = ltId;
			ltr.LineWeight = lw;

			return ltr.ObjectId;
		}

		private bool IsInsideClip(Point3d ptWcs, Extents3d clipBox)
		{
			return ptWcs.X >= clipBox.MinPoint.X && ptWcs.X <= clipBox.MaxPoint.X &&
				   ptWcs.Y >= clipBox.MinPoint.Y && ptWcs.Y <= clipBox.MaxPoint.Y;
		}

		private void AddPointIfInsideClip(Point3d ptWcs, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints)
		{
			if (IsInsideClip(ptWcs, clipBox))
			{
				validPoints.Add(ptWcs.TransformBy(finalTransform));
			}
		}

		private void SetFixedExtensionLine(Dimension dim, double length)
		{
			try
			{
				var type = dim.GetType();
				var propOn = type.GetProperty("Dimfxlon");
				var propLen = type.GetProperty("Dimfxl");

				if (propOn != null && propLen != null)
				{
					propOn.SetValue(dim, true, null);
					propLen.SetValue(dim, length, null);
				}
			}
			catch { }
		}

		private ObjectId CreateOrUpdateCalloutDimStyle(Database db, Transaction tr, double dimScale, string styleName)
		{
			DimStyleTable dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForWrite);
			DimStyleTableRecord dimStyle;

			if (dst.Has(styleName))
			{
				dimStyle = (DimStyleTableRecord)tr.GetObject(dst[styleName], OpenMode.ForWrite);
			}
			else
			{
				dimStyle = new DimStyleTableRecord { Name = styleName };
				dst.Add(dimStyle);
				tr.AddNewlyCreatedDBObject(dimStyle, true);

				dimStyle.Dimtxsty = EnsureTextStyle(db, tr, "Standard", "arial.ttf");
				dimStyle.Dimtxt = 2.0;
				dimStyle.Dimtih = false;
				dimStyle.Dimtoh = true;
				dimStyle.Dimtad = 1;
				dimStyle.Dimgap = 0.6;
				dimStyle.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, 2);
				dimStyle.Dimtfill = 1;

				dimStyle.Dimscale = dimScale;
				dimStyle.Dimasz = 1.0;
				dimStyle.Dimexo = 0.0;
				dimStyle.Dimdli = 6.0;
				dimStyle.Dimexe = 1.0;
				dimStyle.Dimdle = 1.0;
				dimStyle.Dimcen = 0.09;

				ObjectId archTickId = GetArrowBlockId(db, tr, "_ARCHTICK");
				if (!archTickId.IsNull) dimStyle.Dimblk = archTickId;

				dimStyle.Dimlunit = 2;
				dimStyle.Dimdec = 1;
				dimStyle.Dimtdec = 1;
				dimStyle.Dimdsep = '.';
				dimStyle.Dimzin = 8;

				dimStyle.Dimclrd = Color.FromColorIndex(ColorMethod.ByBlock, 0);
				dimStyle.Dimclre = Color.FromColorIndex(ColorMethod.ByBlock, 0);
				dimStyle.Dimlwd = LineWeight.ByBlock;
				dimStyle.Dimlwe = LineWeight.ByBlock;
			}

			return dimStyle.ObjectId;
		}

		private ObjectId EnsureTextStyle(Database db, Transaction tr, string styleName, string fontName)
		{
			TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
			if (tst.Has(styleName)) return tst[styleName];

			TextStyleTableRecord tstr = new TextStyleTableRecord
			{
				Name = styleName,
				FileName = fontName
			};

			tst.Add(tstr);
			tr.AddNewlyCreatedDBObject(tstr, true);
			return tstr.ObjectId;
		}

		private ObjectId GetArrowBlockId(Database db, Transaction tr, string blockName)
		{
			BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
			if (bt.Has(blockName)) return bt[blockName];

			try
			{
				Application.SetSystemVariable("DIMBLK", blockName);
				if (bt.Has(blockName)) return bt[blockName];
			}
			catch { }
			return ObjectId.Null;
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
			SpatialFilter sf = new SpatialFilter { Definition = sfd };

			filterDict.SetAt("SPATIAL", sf);
			tr.AddNewlyCreatedDBObject(sf, true);
		}
	}
}