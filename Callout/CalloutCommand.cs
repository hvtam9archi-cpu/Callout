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
        private static readonly Dictionary<ObjectId, List<ObjectId>> _blockCalloutBubbles = new Dictionary<ObjectId, List<ObjectId>>();

        [CommandMethod("CT", CommandFlags.Modal)]
        public void CreateCallout()
        {
            ExecuteCallout(false);
        }

        [CommandMethod("CT1", CommandFlags.Modal)]
        public void CreateCalloutFar()
        {
            ExecuteCallout(true);
        }

        private void ExecuteCallout(bool isFarCallout)
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            Database database = document.Database;
            Editor editor = document.Editor;

            // NGUYÊN TẮC: Crash-proof Safety (Global try-catch tại Entry Point)
            try
            {
                // 1. NGẮT LIÊN KẾT VỚI TRANSACTION: HỏI UI NGAY TỪ ĐẦU
                var promptBlockOptions = new PromptEntityOptions("\nChọn Block cần trích: ");
                promptBlockOptions.SetRejectMessage("\nChỉ chọn BlockReference!");
                promptBlockOptions.AddAllowedClass(typeof(BlockReference), true);
                var promptBlockResult = editor.GetEntity(promptBlockOptions);
                if (promptBlockResult.Status != PromptStatus.OK) return;

                var promptPolylineOptions = new PromptEntityOptions("\nChọn Polyline làm khung trích: ");
                promptPolylineOptions.SetRejectMessage("\nChỉ chọn Polyline!");
                promptPolylineOptions.AddAllowedClass(typeof(Polyline), true);
                var promptPolylineResult = editor.GetEntity(promptPolylineOptions);
                if (promptPolylineResult.Status != PromptStatus.OK) return;

                ObjectId sourceBlockId = promptBlockResult.ObjectId;
                ObjectId boundaryPolylineId = promptPolylineResult.ObjectId;
                
                int tempCounter = 1;
                string viewNumberValue = "";
                ObjectId _newBubbleId = ObjectId.Null;

                ObjectId anonymousBlockId = ObjectId.Null;
                ObjectId sourceLayerId = ObjectId.Null;
                Extents3d originalExtents = new Extents3d();
                Point3d basePoint = Point3d.Origin;

                // 2. TRANSACTION ĐỢT 1: Lấy thông tin cơ bản và tạo Khung rỗng (Nhỏ, Đóng ngay)
                using (DocumentLock docLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    using (BlockReference sourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference)
                    using (Polyline boundaryPolyline = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Polyline)
                    {
                        if (sourceBlock == null || boundaryPolyline == null) return;

                        if (isFarCallout)
                        {
                            tempCounter = 1;
                            if (_blockCalloutBubbles.TryGetValue(sourceBlockId, out List<ObjectId> bubbleList))
                            {
                                List<int> usedNumbers = new List<int>();
                                List<ObjectId> validBubbles = new List<ObjectId>();
                                foreach (ObjectId bId in bubbleList)
                                {
                                    if (bId.IsErased || !bId.IsValid || bId.IsNull) continue;
                                    BlockReference bRef = transaction.GetObject(bId, OpenMode.ForRead, false, true) as BlockReference;
                                    if (bRef != null && !bRef.IsErased)
                                    {
                                        validBubbles.Add(bId);
                                        string vn = GetAttributeValue(transaction, bRef, "VIEWNUMBER");
                                        if (!string.IsNullOrEmpty(vn) && vn.StartsWith("CT", System.StringComparison.OrdinalIgnoreCase)) 
                                        {
                                            if (int.TryParse(vn.Substring(2), out int num)) 
                                            {
                                                usedNumbers.Add(num);
                                            }
                                        }
                                    }
                                }
                                _blockCalloutBubbles[sourceBlockId] = validBubbles;

                                while (usedNumbers.Contains(tempCounter))
                                {
                                    tempCounter++;
                                }
                            }
                            viewNumberValue = $"CT{tempCounter:D2}";
                        }

                        sourceLayerId = boundaryPolyline.LayerId;
                        originalExtents = boundaryPolyline.GeometricExtents;

                        basePoint = new Point3d(
                            (originalExtents.MinPoint.X + originalExtents.MaxPoint.X) / 2.0,
                            (originalExtents.MinPoint.Y + originalExtents.MaxPoint.Y) / 2.0,
                            (originalExtents.MinPoint.Z + originalExtents.MaxPoint.Z) / 2.0
                        );

                        using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite))
                        using (BlockTableRecord anonymousBlockRecord = new BlockTableRecord { Name = "*U" })
                        {
                            anonymousBlockId = blockTable.Add(anonymousBlockRecord);
                            transaction.AddNewlyCreatedDBObject(anonymousBlockRecord, true);

                            using (BlockReference innerBlock = sourceBlock.Clone() as BlockReference)
                            using (Polyline innerPolyline = boundaryPolyline.Clone() as Polyline)
                            {
                                innerPolyline.LayerId = sourceLayerId;

                                Vector3d vectorToOrigin = basePoint.GetVectorTo(Point3d.Origin);
                                innerBlock.TransformBy(Matrix3d.Displacement(vectorToOrigin));
                                innerPolyline.TransformBy(Matrix3d.Displacement(vectorToOrigin));

                                anonymousBlockRecord.AppendEntity(innerBlock);
                                transaction.AddNewlyCreatedDBObject(innerBlock, true);
                                
                                anonymousBlockRecord.AppendEntity(innerPolyline);
                                transaction.AddNewlyCreatedDBObject(innerPolyline, true);

                                ApplyXClip(transaction, innerBlock, innerPolyline);
                            }
                        }
                    }
                    transaction.Commit();
                }

                // 3. XỬ LÝ KHÔNG GIAN BÊN NGOÀI TRANSACTION (Drag Jig)
                PromptResult jigResult = null;
                double calloutScale = 1.0;
                
                using (BlockReference jigReference = new BlockReference(basePoint, anonymousBlockId) { LayerId = sourceLayerId })
                {
                    var calloutJig = new CalloutJig(jigReference, basePoint, originalExtents, isFarCallout);
                    
                    try
                    {
                        JigInputHandler.Start();
                        jigResult = editor.Drag(calloutJig);
                        calloutScale = JigInputHandler.CurrentScale;
                    }
                    finally
                    {
                        // Đảm bảo không Rò rỉ Event bộ nhớ nếu Editor.Drag bị Exception
                        JigInputHandler.Stop();
                    }

                    if (jigResult.Status != PromptStatus.OK) return;

                    PromptDoubleOptions promptDimScaleOptions = new PromptDoubleOptions("\nDimscale chi tiết trích: ")
                    {
                        AllowZero = false,
                        AllowNegative = false,
                        DefaultValue = calloutScale,
                        UseDefaultValue = true
                    };

                    PromptDoubleResult promptDimScaleResult = editor.GetDouble(promptDimScaleOptions);
                    if (promptDimScaleResult.Status != PromptStatus.OK) return;

                    double dimensionScale = promptDimScaleResult.Value;
                    Point3d finalPosition = calloutJig.CurrentPosition;
                    Matrix3d mathTransform = calloutJig.MathTransform;

                    Point3d sourceBubblePos = basePoint;
                    if (isFarCallout)
                    {
                        sourceBubblePos = new Point3d(originalExtents.MaxPoint.X + (12.0 * dimensionScale), originalExtents.MaxPoint.Y + (12.0 * dimensionScale), 0);
                    }

                    // 4. TRANSACTION ĐỢT 2: Thêm Viewport Block + Thêm Dimensions (Thao tác DB rồi đóng ngay)
                    using (DocumentLock docLock = document.LockDocument())
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        ObjectId backgroundLayerId = EnsureLayer(database, transaction, "Nen", Color.FromRgb(150, 150, 150), "Continuous", LineWeight.LineWeight005);
                        ObjectId dimensionLayerId = EnsureLayer(database, transaction, "ABC_A_Kichthuoc", Color.FromColorIndex(ColorMethod.ByAci, 8), "Continuous", LineWeight.LineWeight009);

                        using (BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite))
                        {
                            jigReference.Position = finalPosition;
                            jigReference.ScaleFactors = new Scale3d(calloutScale);
                            currentSpace.AppendEntity(jigReference);
                            transaction.AddNewlyCreatedDBObject(jigReference, true);

                            if (!isFarCallout)
                            {
                                double dotDiameter = 1.5 * dimensionScale;
                                var leaderEntities = CalloutGeometryService.CreateSmartLeader(originalExtents, mathTransform, dotDiameter);
                                foreach (Entity ent in leaderEntities)
                                {
                                    ent.LayerId = sourceLayerId;
                                    currentSpace.AppendEntity(ent);
                                    transaction.AddNewlyCreatedDBObject(ent, true);
                                }
                            }
                            else
                            {
                                ObjectId detailBlockId = EnsureDetailCalloutBlock(database, transaction);
                                if (!detailBlockId.IsNull)
                                {
                                    using (Polyline originalBoundaryPolyline = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Polyline)
                                    {
                                        Point3d closestPoint = originalBoundaryPolyline.GetClosestPointTo(sourceBubblePos, false);
                                        
                                        double bubbleRadius = 6.0 * dimensionScale;
                                        Point3d leaderEnd = new Point3d(sourceBubblePos.X - bubbleRadius, sourceBubblePos.Y, 0);
                                        double kneeX = Math.Max(leaderEnd.X - (4.0 * dimensionScale), closestPoint.X + (2.0 * dimensionScale));
                                        Point3d leaderKnee = new Point3d(kneeX, sourceBubblePos.Y, 0);
                                        
                                        using (Polyline bubbleLeader = new Polyline())
                                        {
                                            bubbleLeader.SetDatabaseDefaults();
                                            bubbleLeader.AddVertexAt(0, new Point2d(closestPoint.X, closestPoint.Y), 0, 0, 0);
                                            bubbleLeader.AddVertexAt(1, new Point2d(leaderKnee.X, leaderKnee.Y), 0, 0, 0);
                                            bubbleLeader.AddVertexAt(2, new Point2d(leaderEnd.X, leaderEnd.Y), 0, 0, 0);
                                            bubbleLeader.LayerId = sourceLayerId;
                                            currentSpace.AppendEntity(bubbleLeader);
                                            transaction.AddNewlyCreatedDBObject(bubbleLeader, true);
                                        }

                                        using (Entity dot = CreateLeaderDot(closestPoint, 2.0 * dimensionScale))
                                        {
                                            dot.LayerId = sourceLayerId;
                                            currentSpace.AppendEntity(dot);
                                            transaction.AddNewlyCreatedDBObject(dot, true);
                                        }
                                    }

                                    using (BlockReference sourceBubble = new BlockReference(sourceBubblePos, detailBlockId))
                                    {
                                        sourceBubble.ScaleFactors = new Scale3d(dimensionScale);
                                        sourceBubble.LayerId = sourceLayerId;
                                        currentSpace.AppendEntity(sourceBubble);
                                        transaction.AddNewlyCreatedDBObject(sourceBubble, true);
                                        
                                        var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
                                            { "VIEWNUMBER", viewNumberValue },
                                            { "SHEETNUMBER", "" }
                                        };
                                        ApplyDictionaryAttributes(transaction, sourceBubble, dict);
                                    }
                                }
                                else
                                {
                                    editor.WriteMessage("\n[Cảnh báo]: Không thể tạo Block '_DetailCallout - Metric'!");
                                }
                            }

                            string styleName = $"TB_ABC_DIM 1-{dimensionScale:0.##}";
                            ObjectId dimensionStyleId = EnsureCalloutDimensionStyle(database, transaction, dimensionScale, styleName);

                            using (BlockReference originalSourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference)
                            using (Polyline originalBoundaryPolyline = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Polyline)
                            {
                                double dimCollisionOffset = CreateAutoDimensionGeometry(
                                    originalSourceBlock, originalBoundaryPolyline, mathTransform,
                                    currentSpace, dimensionStyleId, dimensionScale, calloutScale,
                                    basePoint, finalPosition, backgroundLayerId, dimensionLayerId, transaction
                                );

                                if (isFarCallout)
                                {
                                    ObjectId detailBlockId = EnsureDetailCalloutBlock(database, transaction);
                                    if (!detailBlockId.IsNull)
                                    {
                                        Extents3d finalExtents;
                                        using (Polyline clonedBoundary = originalBoundaryPolyline.Clone() as Polyline)
                                        {
                                            clonedBoundary.TransformBy(mathTransform);
                                            finalExtents = clonedBoundary.GeometricExtents;
                                        }

                                        double titleY = finalExtents.MinPoint.Y - (10.0 * dimensionScale) - dimCollisionOffset;
                                        
                                        double textWidth = 28.0 * dimensionScale; // safe fallback width for "CHI TIẾT "
                                        double gap = 3.5 * dimensionScale;
                                        double bubbleRadius = 6.0 * dimensionScale;
                                        double totalWidth = textWidth + gap + (2.0 * bubbleRadius);
                                        double centerX = (finalExtents.MinPoint.X + finalExtents.MaxPoint.X) / 2.0;

                                        double startX = centerX - (totalWidth / 2.0);
                                        Point3d titleStartPoint = new Point3d(startX, titleY, 0);

                                        using (DBText textTitle = new DBText())
                                        {
                                            textTitle.SetDatabaseDefaults();
                                            textTitle.Position = titleStartPoint;
                                            textTitle.HorizontalMode = TextHorizontalMode.TextLeft;
                                            textTitle.VerticalMode = TextVerticalMode.TextVerticalMid;
                                            textTitle.AlignmentPoint = titleStartPoint;
                                            textTitle.Height = 4.0 * dimensionScale;
                                            textTitle.TextString = "CHI TIẾT ";
                                            textTitle.LayerId = sourceLayerId;
                                            textTitle.TextStyleId = EnsureTextStyle(database, transaction, "Standard", "arial.ttf");
                                            
                                            // Get precise width if bounds available
                                            currentSpace.AppendEntity(textTitle);
                                            transaction.AddNewlyCreatedDBObject(textTitle, true);
                                            
                                            try {
                                                if (textTitle.Bounds.HasValue) {
                                                    textWidth = textTitle.Bounds.Value.MaxPoint.X - textTitle.Bounds.Value.MinPoint.X;
                                                }
                                            } catch { }
                                        }

                                        Point3d titleBubblePos = new Point3d(titleStartPoint.X + textWidth + gap + bubbleRadius, titleY, 0);

                                        using (BlockReference titleBubble = new BlockReference(titleBubblePos, detailBlockId))
                                        {
                                            titleBubble.ScaleFactors = new Scale3d(dimensionScale);
                                            titleBubble.LayerId = sourceLayerId;
                                            currentSpace.AppendEntity(titleBubble);
                                            transaction.AddNewlyCreatedDBObject(titleBubble, true);
                                            
                                            var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
                                                { "VIEWNUMBER", viewNumberValue },
                                                { "SHEETNUMBER", "" }
                                            };
                                            ApplyDictionaryAttributes(transaction, titleBubble, dict);
                                            _newBubbleId = titleBubble.ObjectId;
                                        }
                                    }
                                }
                            }
                        }
                        transaction.Commit();
                    }

                    if (isFarCallout && !_newBubbleId.IsNull)
                    {
                        if (!_blockCalloutBubbles.ContainsKey(sourceBlockId))
                            _blockCalloutBubbles[sourceBlockId] = new List<ObjectId>();
                        _blockCalloutBubbles[sourceBlockId].Add(_newBubbleId);
                    }
                }
            }
            catch (System.Exception ex)
            {
                // NGUYÊN TẮC: Ghi lỗi trực quan, triệt để thay vì im lặng
                editor.WriteMessage($"\n[ERROR Lỗi Hệ Thống]: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        private void ExtractGeometryRecursive(ObjectId blockRecordId, Matrix3d matrix, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints, List<Arc> validArcs, Transaction transaction)
        {
            using (BlockTableRecord blockRecord = (BlockTableRecord)transaction.GetObject(blockRecordId, OpenMode.ForRead))
            {
                foreach (ObjectId entityId in blockRecord)
                {
                    using (Entity entity = transaction.GetObject(entityId, OpenMode.ForRead) as Entity)
                    {
                        if (entity == null || !entity.Visible) continue;

                        if (entity is BlockReference blockReference)
                        {
                            Matrix3d newMatrix = matrix * blockReference.BlockTransform;
                            ObjectId targetRecordId = blockReference.IsDynamicBlock ? blockReference.DynamicBlockTableRecord : blockReference.BlockTableRecord;
                            ExtractGeometryRecursive(targetRecordId, newMatrix, clipBox, finalTransform, validPoints, validArcs, transaction);
                        }
                        else if (entity is Line line)
                        {
                            AddPointIfInsideClip(line.StartPoint.TransformBy(matrix), clipBox, finalTransform, validPoints);
                            AddPointIfInsideClip(line.EndPoint.TransformBy(matrix), clipBox, finalTransform, validPoints);
                        }
                        else if (entity is Polyline polyline)
                        {
                            for (int i = 0; i < polyline.NumberOfVertices; i++)
                            {
                                AddPointIfInsideClip(polyline.GetPoint3dAt(i).TransformBy(matrix), clipBox, finalTransform, validPoints);
                            }
                        }
                        else if (entity is Polyline2d polyline2d)
                        {
                            foreach (ObjectId vertexId in polyline2d)
                            {
                                using (Vertex2d vertex = transaction.GetObject(vertexId, OpenMode.ForRead) as Vertex2d)
                                {
                                    if (vertex != null) AddPointIfInsideClip(vertex.Position.TransformBy(matrix), clipBox, finalTransform, validPoints);
                                }
                            }
                        }
                        else if (entity is Circle circle)
                        {
                            Point3d centerWcs = circle.Center.TransformBy(matrix);
                            double r = circle.Radius * matrix.GetScale();
                            AddPointIfInsideClip(centerWcs + new Vector3d(r, 0, 0), clipBox, finalTransform, validPoints);
                            AddPointIfInsideClip(centerWcs + new Vector3d(-r, 0, 0), clipBox, finalTransform, validPoints);
                            AddPointIfInsideClip(centerWcs + new Vector3d(0, r, 0), clipBox, finalTransform, validPoints);
                            AddPointIfInsideClip(centerWcs + new Vector3d(0, -r, 0), clipBox, finalTransform, validPoints);
                        }
                        else if (entity is Arc arc)
                        {
                            Point3d midPointWcs = arc.GetPointAtParameter(arc.StartParam + (arc.EndParam - arc.StartParam) / 2.0).TransformBy(matrix);
                            if (IsInsideClip(midPointWcs, clipBox))
                            {
                                Arc clonedArc = arc.Clone() as Arc;
                                clonedArc.TransformBy(matrix * finalTransform);
                                validArcs.Add(clonedArc);
                            }
                        }
                    }
                }
            }
        }

        private double CreateAutoDimensionGeometry(BlockReference sourceBlock, Polyline boundaryPolyline, Matrix3d finalTransform, BlockTableRecord currentSpace, ObjectId dimensionStyleId, double dimensionScale, double calloutScale, Point3d originalCenter, Point3d newCenter, ObjectId backgroundLayerId, ObjectId dimensionLayerId, Transaction transaction)
        {
            List<Point3d> validPoints = new List<Point3d>();
            List<Arc> validArcs = new List<Arc>();
            ObjectIdCollection backgroundObjectIds = new ObjectIdCollection();
            double extraBottomOffset = 0.0;

            try
            {
                Extents3d clipBox = boundaryPolyline.GeometricExtents;
                ExtractGeometryRecursive(sourceBlock.BlockTableRecord, sourceBlock.BlockTransform, clipBox, finalTransform, validPoints, validArcs, transaction);

                Extents3d finalBoundaryExtents;
                using (Polyline clonedBoundary = boundaryPolyline.Clone() as Polyline)
                {
                    clonedBoundary.TransformBy(finalTransform);
                    finalBoundaryExtents = clonedBoundary.GeometricExtents;
                }

                bool isPlaceTop = newCenter.Y >= originalCenter.Y;
                double referenceY = isPlaceTop ? finalBoundaryExtents.MaxPoint.Y : finalBoundaryExtents.MinPoint.Y;
                double directionY = isPlaceTop ? 1.0 : -1.0;

                bool isPlaceRight = newCenter.X >= originalCenter.X;
                double referenceX = isPlaceRight ? finalBoundaryExtents.MaxPoint.X : finalBoundaryExtents.MinPoint.X;
                double directionX = isPlaceRight ? 1.0 : -1.0;

                double firstLayerOffset = 8.0 * dimensionScale;
                double secondLayerOffset = firstLayerOffset + (6.0 * dimensionScale);
                double dimensionLinearFactor = 1.0 / calloutScale;

                foreach (Arc validArc in validArcs)
                {
                    Point3d midPoint = validArc.GetPointAtParameter(validArc.StartParam + (validArc.EndParam - validArc.StartParam) / 2.0);
                    Vector3d directionToMid = validArc.Center.GetVectorTo(midPoint).GetNormal();
                    Point3d targetArcPoint = validArc.Center + directionToMid * (validArc.Radius + firstLayerOffset);

                    using (ArcDimension arcDimension = new ArcDimension(
                        validArc.Center, validArc.StartPoint, validArc.EndPoint, targetArcPoint, "", dimensionStyleId))
                    {
                        arcDimension.LayerId = dimensionLayerId;
                        arcDimension.SetDatabaseDefaults();
                        arcDimension.Dimlfac = dimensionLinearFactor;
                        SetFixedExtensionLine(arcDimension, 6.0);

                        currentSpace.AppendEntity(arcDimension);
                        transaction.AddNewlyCreatedDBObject(arcDimension, true);
                    }
                }

                if (validPoints.Count == 0) return extraBottomOffset;

                var distinctX = validPoints.Select(p => Math.Round(p.X, 2)).Distinct().OrderBy(x => x).ToList();
                var distinctY = validPoints.Select(p => Math.Round(p.Y, 2)).Distinct().OrderBy(y => y).ToList();

                double minDimDist = 5.0 * dimensionScale;

                List<double> filteredX = new List<double>();
                if (distinctX.Count > 0)
                {
                    filteredX.Add(distinctX.First());
                    for (int i = 1; i < distinctX.Count; i++)
                    {
                        if (distinctX[i] - filteredX.Last() >= minDimDist) filteredX.Add(distinctX[i]);
                    }
                }
                distinctX = filteredX;

                List<double> filteredY = new List<double>();
                if (distinctY.Count > 0)
                {
                    filteredY.Add(distinctY.First());
                    for (int i = 1; i < distinctY.Count; i++)
                    {
                        if (distinctY[i] - filteredY.Last() >= minDimDist) filteredY.Add(distinctY[i]);
                    }
                }
                distinctY = filteredY;

                if (!isPlaceTop)
                {
                    if (distinctX.Count > 2) extraBottomOffset = secondLayerOffset + (4.0 * dimensionScale);
                    else if (distinctX.Count > 1) extraBottomOffset = firstLayerOffset + (4.0 * dimensionScale);
                }

                if (distinctX.Count > 1)
                {
                    foreach (double xValue in distinctX)
                    {
                        using (Line guideLine = new Line(new Point3d(xValue, finalBoundaryExtents.MinPoint.Y, 0), new Point3d(xValue, finalBoundaryExtents.MaxPoint.Y, 0)))
                        {
                            guideLine.LayerId = backgroundLayerId;
                            currentSpace.AppendEntity(guideLine);
                            transaction.AddNewlyCreatedDBObject(guideLine, true);
                            backgroundObjectIds.Add(guideLine.ObjectId);
                        }
                    }

                    for (int i = 0; i < distinctX.Count - 1; i++)
                    {
                        using (RotatedDimension dimensionX = new RotatedDimension())
                        {
                            dimensionX.LayerId = dimensionLayerId;
                            dimensionX.SetDatabaseDefaults();
                            dimensionX.DimensionStyle = dimensionStyleId;
                            dimensionX.Dimlfac = dimensionLinearFactor;
                            SetFixedExtensionLine(dimensionX, 6.0);

                            dimensionX.XLine1Point = new Point3d(distinctX[i], referenceY, 0);
                            dimensionX.XLine2Point = new Point3d(distinctX[i + 1], referenceY, 0);
                            dimensionX.DimLinePoint = new Point3d(distinctX[i], referenceY + (directionY * firstLayerOffset), 0);
                            dimensionX.Rotation = 0.0;
                            currentSpace.AppendEntity(dimensionX);
                            transaction.AddNewlyCreatedDBObject(dimensionX, true);
                        }
                    }

                    if (distinctX.Count > 2)
                    {
                        using (RotatedDimension totalDimensionX = new RotatedDimension())
                        {
                            totalDimensionX.LayerId = dimensionLayerId;
                            totalDimensionX.SetDatabaseDefaults();
                            totalDimensionX.DimensionStyle = dimensionStyleId;
                            totalDimensionX.Dimlfac = dimensionLinearFactor;
                            SetFixedExtensionLine(totalDimensionX, 6.0);

                            totalDimensionX.XLine1Point = new Point3d(distinctX.First(), referenceY, 0);
                            totalDimensionX.XLine2Point = new Point3d(distinctX.Last(), referenceY, 0);
                            totalDimensionX.DimLinePoint = new Point3d(distinctX.First(), referenceY + (directionY * secondLayerOffset), 0);
                            totalDimensionX.Rotation = 0.0;
                            currentSpace.AppendEntity(totalDimensionX);
                            transaction.AddNewlyCreatedDBObject(totalDimensionX, true);
                        }
                    }
                }

                if (distinctY.Count > 1)
                {
                    foreach (double yValue in distinctY)
                    {
                        using (Line guideLine = new Line(new Point3d(finalBoundaryExtents.MinPoint.X, yValue, 0), new Point3d(finalBoundaryExtents.MaxPoint.X, yValue, 0)))
                        {
                            guideLine.LayerId = backgroundLayerId;
                            currentSpace.AppendEntity(guideLine);
                            transaction.AddNewlyCreatedDBObject(guideLine, true);
                            backgroundObjectIds.Add(guideLine.ObjectId);
                        }
                    }

                    for (int i = 0; i < distinctY.Count - 1; i++)
                    {
                        using (RotatedDimension dimensionY = new RotatedDimension())
                        {
                            dimensionY.LayerId = dimensionLayerId;
                            dimensionY.SetDatabaseDefaults();
                            dimensionY.DimensionStyle = dimensionStyleId;
                            dimensionY.Dimlfac = dimensionLinearFactor;
                            SetFixedExtensionLine(dimensionY, 6.0);

                            dimensionY.XLine1Point = new Point3d(referenceX, distinctY[i], 0);
                            dimensionY.XLine2Point = new Point3d(referenceX, distinctY[i + 1], 0);
                            dimensionY.DimLinePoint = new Point3d(referenceX + (directionX * firstLayerOffset), distinctY[i], 0);
                            dimensionY.Rotation = Math.PI / 2.0;
                            currentSpace.AppendEntity(dimensionY);
                            transaction.AddNewlyCreatedDBObject(dimensionY, true);
                        }
                    }

                    if (distinctY.Count > 2)
                    {
                        using (RotatedDimension totalDimensionY = new RotatedDimension())
                        {
                            totalDimensionY.LayerId = dimensionLayerId;
                            totalDimensionY.SetDatabaseDefaults();
                            totalDimensionY.DimensionStyle = dimensionStyleId;
                            totalDimensionY.Dimlfac = dimensionLinearFactor;
                            SetFixedExtensionLine(totalDimensionY, 6.0);

                            totalDimensionY.XLine1Point = new Point3d(referenceX, distinctY.First(), 0);
                            totalDimensionY.XLine2Point = new Point3d(referenceX, distinctY.Last(), 0);
                            totalDimensionY.DimLinePoint = new Point3d(referenceX + (directionX * secondLayerOffset), distinctY.First(), 0);
                            totalDimensionY.Rotation = Math.PI / 2.0;
                            currentSpace.AppendEntity(totalDimensionY);
                            transaction.AddNewlyCreatedDBObject(totalDimensionY, true);
                        }
                    }
                }

                // Thực hiện MoveToBottom cho tất cả các Line nền
                if (backgroundObjectIds.Count > 0)
                {
                    using (DrawOrderTable drawOrderTable = (DrawOrderTable)transaction.GetObject(currentSpace.DrawOrderTableId, OpenMode.ForWrite))
                    {
                        drawOrderTable.MoveToBottom(backgroundObjectIds);
                    }
                }
            }
            finally
            {
                // NGUYÊN TẮC: Clear triệt để các Entities tạm thời đã tạo trong List
                foreach (var arc in validArcs)
                {
                    if (arc != null && !arc.IsDisposed) arc.Dispose();
                }
            }
            
            return extraBottomOffset;
        }

        // ==========================================
        // CÁC HÀM HELPER ĐỘC LẬP
        // ==========================================
        private ObjectId EnsureLayer(Database database, Transaction transaction, string layerName, Color color, string lineTypeName, LineWeight lineWeight)
        {
            using (LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForWrite))
            {
                ObjectId lineTypeId = SymbolUtilityServices.GetLinetypeContinuousId(database);

                if (!string.IsNullOrEmpty(lineTypeName))
                {
                    using (LinetypeTable linetypeTable = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead))
                    {
                        if (!linetypeTable.Has(lineTypeName))
                        {
                            string filename = (short)Application.GetSystemVariable("MEASUREMENT") == 0 ? "acad.lin" : "acadiso.lin";
                            try
                            {
                                database.LoadLineTypeFile(lineTypeName, filename);
                            }
                            catch (System.Exception ex)
                            {
                                // Không giấu lỗi
                                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[Cảnh báo] Load Linetype '{lineTypeName}' lỗi: {ex.Message}");
                            }
                        }

                        if (linetypeTable.Has(lineTypeName))
                        {
                            lineTypeId = linetypeTable[lineTypeName];
                        }
                    }
                }

                if (layerTable.Has(layerName))
                {
                    using (LayerTableRecord layerRecord = (LayerTableRecord)transaction.GetObject(layerTable[layerName], OpenMode.ForWrite))
                    {
                        layerRecord.Color = color;
                        layerRecord.LinetypeObjectId = lineTypeId;
                        layerRecord.LineWeight = lineWeight;
                        return layerRecord.ObjectId;
                    }
                }
                else
                {
                    using (LayerTableRecord layerRecord = new LayerTableRecord { Name = layerName })
                    {
                        layerTable.Add(layerRecord);
                        transaction.AddNewlyCreatedDBObject(layerRecord, true);

                        layerRecord.Color = color;
                        layerRecord.LinetypeObjectId = lineTypeId;
                        layerRecord.LineWeight = lineWeight;
                        
                        return layerRecord.ObjectId;
                    }
                }
            }
        }

        private bool IsInsideClip(Point3d pointWcs, Extents3d clipBox)
        {
            return pointWcs.X >= clipBox.MinPoint.X && pointWcs.X <= clipBox.MaxPoint.X &&
                   pointWcs.Y >= clipBox.MinPoint.Y && pointWcs.Y <= clipBox.MaxPoint.Y;
        }

        private void AddPointIfInsideClip(Point3d pointWcs, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints)
        {
            if (IsInsideClip(pointWcs, clipBox))
            {
                validPoints.Add(pointWcs.TransformBy(finalTransform));
            }
        }

        private void SetFixedExtensionLine(Dimension dimension, double length)
        {
            try
            {
                var type = dimension.GetType();
                var propertyOn = type.GetProperty("Dimfxlon");
                var propertyLength = type.GetProperty("Dimfxl");

                if (propertyOn != null && propertyLength != null)
                {
                    propertyOn.SetValue(dimension, true, null);
                    propertyLength.SetValue(dimension, length, null);
                }
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[Cảnh báo] Reflection Dimension (SetFixedExtensionLine) lỗi: {ex.Message}");
            }
        }

        private ObjectId EnsureCalloutDimensionStyle(Database database, Transaction transaction, double dimensionScale, string styleName)
        {
            using (DimStyleTable dimensionTable = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForWrite))
            {
                if (dimensionTable.Has(styleName))
                {
                    using (DimStyleTableRecord existingRecord = (DimStyleTableRecord)transaction.GetObject(dimensionTable[styleName], OpenMode.ForWrite))
                    {
                        return existingRecord.ObjectId;
                    }
                }

                using (DimStyleTableRecord dimensionStyle = new DimStyleTableRecord { Name = styleName })
                {
                    dimensionTable.Add(dimensionStyle);
                    transaction.AddNewlyCreatedDBObject(dimensionStyle, true);

                    dimensionStyle.Dimtxsty = EnsureTextStyle(database, transaction, "Standard", "arial.ttf");
                    dimensionStyle.Dimtxt = 2.0;
                    dimensionStyle.Dimtih = false;
                    dimensionStyle.Dimtoh = true;
                    dimensionStyle.Dimtad = 1;
                    dimensionStyle.Dimgap = 0.6;
                    dimensionStyle.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, 2);
                    dimensionStyle.Dimtfill = 1;

                    dimensionStyle.Dimscale = dimensionScale;
                    dimensionStyle.Dimasz = 1.0;
                    dimensionStyle.Dimexo = 0.0;
                    dimensionStyle.Dimdli = 6.0;
                    dimensionStyle.Dimexe = 1.0;
                    dimensionStyle.Dimdle = 1.0;
                    dimensionStyle.Dimcen = 0.09;

                    // Set Fixed Extension Line on the DB Style directly
                    try
                    {
                        var propOn = dimensionStyle.GetType().GetProperty("Dimfxlon");
                        var propLen = dimensionStyle.GetType().GetProperty("Dimfxl");
                        propOn?.SetValue(dimensionStyle, true, null);
                        propLen?.SetValue(dimensionStyle, 6.0, null);
                    }
                    catch { }

                    ObjectId architecturalTickId = GetArrowBlockId(database, transaction, "_ARCHTICK");
                    if (!architecturalTickId.IsNull) dimensionStyle.Dimblk = architecturalTickId;

                    dimensionStyle.Dimlunit = 2;
                    dimensionStyle.Dimdec = 1;
                    dimensionStyle.Dimtdec = 1;
                    dimensionStyle.Dimdsep = '.';
                    dimensionStyle.Dimzin = 8;

                    dimensionStyle.Dimclrd = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    dimensionStyle.Dimclre = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    dimensionStyle.Dimlwd = LineWeight.ByBlock;
                    dimensionStyle.Dimlwe = LineWeight.ByBlock;

                    return dimensionStyle.ObjectId;
                }
            }
        }

        private ObjectId EnsureTextStyle(Database database, Transaction transaction, string styleName, string fontName)
        {
            using (TextStyleTable textStyleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForWrite))
            {
                if (textStyleTable.Has(styleName)) return textStyleTable[styleName];

                using (TextStyleTableRecord textStyleRecord = new TextStyleTableRecord())
                {
                    textStyleRecord.Name = styleName;
                    textStyleRecord.FileName = fontName;

                    textStyleTable.Add(textStyleRecord);
                    transaction.AddNewlyCreatedDBObject(textStyleRecord, true);
                    return textStyleRecord.ObjectId;
                }
            }
        }

        private ObjectId GetArrowBlockId(Database database, Transaction transaction, string blockName)
        {
            using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead))
            {
                if (blockTable.Has(blockName)) return blockTable[blockName];

                try
                {
                    Application.SetSystemVariable("DIMBLK", blockName);
                    if (blockTable.Has(blockName)) return blockTable[blockName];
                }
                catch (System.Exception ex)
                {
                    Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[Cảnh báo] Lấy DB Arrow Block {blockName} lỗi: {ex.Message}");
                }
                return ObjectId.Null;
            }
        }

        private ObjectId GetBlockIdByNameRobust(Database database, Transaction transaction, params string[] names)
        {
            using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead))
            {
                foreach (string name in names)
                {
                    if (blockTable.Has(name)) return blockTable[name];
                }
            }
            return ObjectId.Null;
        }

        private void ApplyXClip(Transaction transaction, BlockReference blockReference, Polyline polyline)
        {
            blockReference.CreateExtensionDictionary();
            using (DBDictionary extensionDictionary = (DBDictionary)transaction.GetObject(blockReference.ExtensionDictionary, OpenMode.ForWrite))
            {
                using (DBDictionary filterDictionary = new DBDictionary())
                {
                    extensionDictionary.SetAt("ACAD_FILTER", filterDictionary);
                    transaction.AddNewlyCreatedDBObject(filterDictionary, true);

                    Matrix3d worldToBlockMatrix = blockReference.BlockTransform.Inverse();
                    Point2dCollection points = new Point2dCollection();
                    for (int i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        Point3d pointInBlock = polyline.GetPoint3dAt(i).TransformBy(worldToBlockMatrix);
                        points.Add(new Point2d(pointInBlock.X, pointInBlock.Y));
                    }

                    SpatialFilterDefinition spatialFilterDefinition = new SpatialFilterDefinition(points, Vector3d.ZAxis, 0.0, 0.0, 0.0, true);
                    using (SpatialFilter spatialFilter = new SpatialFilter { Definition = spatialFilterDefinition })
                    {
                        filterDictionary.SetAt("SPATIAL", spatialFilter);
                        transaction.AddNewlyCreatedDBObject(spatialFilter, true);
                    }
                }
            }
        }

        private ObjectId EnsureDetailCalloutBlock(Database database, Transaction transaction)
        {
            string blockName = "_DetailCallout - Metric";
            using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite))
            {
                if (blockTable.Has(blockName)) return blockTable[blockName];

                using (BlockTableRecord btr = new BlockTableRecord())
                {
                    btr.Name = blockName;
                    btr.Origin = Point3d.Origin;
                    
                    Circle c1 = new Circle(Point3d.Origin, Vector3d.ZAxis, 5.5);
                    c1.SetDatabaseDefaults();
                    btr.AppendEntity(c1);

                    Circle c2 = new Circle(Point3d.Origin, Vector3d.ZAxis, 6.0);
                    c2.SetDatabaseDefaults();
                    btr.AppendEntity(c2);

                    Line l1 = new Line(new Point3d(-6.0, 0, 0), new Point3d(6.0, 0, 0));
                    l1.SetDatabaseDefaults();
                    btr.AppendEntity(l1);

                    ObjectId abcVerdanaStyleId = EnsureTextStyle(database, transaction, "ABC_Verdana", "verdana.ttf");

                    AttributeDefinition ad1 = new AttributeDefinition();
                    ad1.SetDatabaseDefaults();
                    ad1.HorizontalMode = TextHorizontalMode.TextCenter;
                    ad1.VerticalMode = TextVerticalMode.TextBase;
                    ad1.AlignmentPoint = new Point3d(0, 1.5, 0);
                    ad1.Position = new Point3d(0, 1.5, 0);
                    ad1.Height = 2.5;
                    ad1.Tag = "VIEWNUMBER";
                    ad1.TextString = "VIEWNUMBER";
                    ad1.Prompt = "Enter view number";
                    ad1.TextStyleId = abcVerdanaStyleId;
                    btr.AppendEntity(ad1);

                    AttributeDefinition ad2 = new AttributeDefinition();
                    ad2.SetDatabaseDefaults();
                    ad2.HorizontalMode = TextHorizontalMode.TextCenter;
                    ad2.VerticalMode = TextVerticalMode.TextVerticalMid;
                    ad2.AlignmentPoint = new Point3d(0, -2.0, 0);
                    ad2.Position = new Point3d(0, -2.0, 0);
                    ad2.Height = 2.0;
                    ad2.Tag = "SHEETNUMBER";
                    ad2.TextString = "SHEETNUMBER";
                    ad2.Prompt = "Enter sheet number";
                    ad2.TextStyleId = abcVerdanaStyleId;
                    btr.AppendEntity(ad2);

                    blockTable.Add(btr);
                    transaction.AddNewlyCreatedDBObject(btr, true);
                    return btr.ObjectId;
                }
            }
        }

        private void ApplyDictionaryAttributes(Transaction transaction, BlockReference blockRef, Dictionary<string, string> attributeValues)
        {
            BlockTableRecord blockDef = (BlockTableRecord)transaction.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
            if (!blockDef.HasAttributeDefinitions) return;

            HashSet<string> existingTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                AttributeReference existingAtt = transaction.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                if (existingAtt != null)
                {
                    existingTags.Add(existingAtt.Tag);
                    if (attributeValues.TryGetValue(existingAtt.Tag, out string newVal))
                    {
                        existingAtt.TextString = newVal;
                    }
                }
            }

            foreach (ObjectId id in blockDef)
            {
                var dbObj = transaction.GetObject(id, OpenMode.ForRead);
                if (dbObj is AttributeDefinition attDef && !attDef.Constant)
                {
                    if (existingTags.Contains(attDef.Tag)) continue;

                    using (AttributeReference attRef = new AttributeReference())
                    {
                        attRef.SetAttributeFromBlock(attDef, blockRef.BlockTransform);
                        attRef.Position = attDef.Position.TransformBy(blockRef.BlockTransform);
                        
                        if (attributeValues.TryGetValue(attDef.Tag, out string tagValue))
                        {
                            attRef.TextString = tagValue;
                        }
                        
                        blockRef.AttributeCollection.AppendAttribute(attRef);
                        transaction.AddNewlyCreatedDBObject(attRef, true);
                    }
                }
            }
        }

        private Entity CreateLeaderDot(Point3d center, double diameter)
        {
            Polyline dot = new Polyline();
            dot.SetDatabaseDefaults();
            double radius = diameter / 2.0;

            dot.AddVertexAt(0, new Point2d(center.X - radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.AddVertexAt(1, new Point2d(center.X + radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.Closed = true;

            return dot;
        }

        private string GetAttributeValue(Transaction tr, BlockReference blockRef, string tag)
        {
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead, false, true) as AttributeReference;
                if (attRef != null && attRef.Tag.Equals(tag, System.StringComparison.OrdinalIgnoreCase))
                {
                    return attRef.TextString;
                }
            }
            return null;
        }
    }
}
