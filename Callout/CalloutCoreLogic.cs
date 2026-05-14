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

namespace Callout.Logic
{
    public class CalloutBubblePair
    {
        public ObjectId SourceBubbleId { get; set; }
        public ObjectId TitleBubbleId { get; set; }
    }

    public static class CalloutCoreLogic
    {
        private static readonly Dictionary<Database, Dictionary<ObjectId, List<CalloutBubblePair>>> _blockCalloutBubbles = new Dictionary<Database, Dictionary<ObjectId, List<CalloutBubblePair>>>();

        private static Callout.UI.CalloutManagerWindow _managerWindow = null;

        public static Dictionary<ObjectId, List<CalloutBubblePair>> GetCalloutMappings(Database db)
        {
            if (db == null) return new Dictionary<ObjectId, List<CalloutBubblePair>>();
            if (!_blockCalloutBubbles.ContainsKey(db))
            {
                _blockCalloutBubbles[db] = new Dictionary<ObjectId, List<CalloutBubblePair>>();
            }
            return _blockCalloutBubbles[db];
        }


        public static void CreateCallout()
        {
            ExecuteCallout(false);
        }



        public static void ConfigureCallout()
        {
            try
            {
                var window = new Callout.UI.CalloutConfigWindow();
                Application.ShowModalWindow(window);
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[ERROR CT2]: {ex.Message}");
            }
        }

        public static void ShowCalloutStatus()
        {
            try
            {
                if (_managerWindow == null)
                {
                    _managerWindow = new Callout.UI.CalloutManagerWindow();
                    _managerWindow.Closed += (s, e) => _managerWindow = null;
                    Application.ShowModelessWindow(_managerWindow);
                }
                else
                {
                    _managerWindow.Activate();
                    _managerWindow.Focus();
                }
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[ERROR CTS]: {ex.Message}");
            }
        }

        public static void CreateCalloutFar()
        {
            ExecuteCallout(true);
        }

        public static void ExecuteCallout(bool isFarCallout)
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

                var promptPolylineOptions = new PromptEntityOptions("\nChọn đối tượng khung trích (Polyline, Đường tròn...): ");
                promptPolylineOptions.SetRejectMessage("\nChỉ chọn các đường khép kín (Polyline, Circle)!");
                promptPolylineOptions.AddAllowedClass(typeof(Curve), false);
                var promptPolylineResult = editor.GetEntity(promptPolylineOptions);
                if (promptPolylineResult.Status != PromptStatus.OK) return;

                ObjectId sourceBlockId = promptBlockResult.ObjectId;
                ObjectId boundaryPolylineId = promptPolylineResult.ObjectId;
                
                int tempCounter = 1;
                string viewNumberValue = "";
                ObjectId _newTitleBubbleId = ObjectId.Null;
                ObjectId _newSourceBubbleId = ObjectId.Null;

                ObjectId detailContentBlockId = ObjectId.Null;
                ObjectId sourceLayerId = ObjectId.Null;
                Extents3d originalExtents = new Extents3d();
                Point3d basePoint = Point3d.Origin;

                // 2. TRANSACTION ĐỢT 1: Lấy thông tin cơ bản và tạo Khung rỗng (Nhỏ, Đóng ngay)
                using (DocumentLock docLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    using (BlockReference sourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference)
                    using (Curve boundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve)
                    {
                        if (sourceBlock == null || boundaryCurve == null) return;
                        
                        if (!boundaryCurve.Closed && !(boundaryCurve is Circle))
                        {
                            editor.WriteMessage("\nCảnh báo: Đối tượng ranh giới không khép kín. XClip có thể không như ý muốn.");
                        }

                        if (isFarCallout)
                        {
                            tempCounter = 1;
                            var mappings = GetCalloutMappings(database);
                            if (mappings.TryGetValue(sourceBlockId, out List<CalloutBubblePair> bubbleList))
                            {
                                List<int> usedNumbers = new List<int>();
                                List<CalloutBubblePair> validBubbles = new List<CalloutBubblePair>();
                                foreach (CalloutBubblePair pair in bubbleList)
                                {
                                    if (pair.TitleBubbleId.IsErased || !pair.TitleBubbleId.IsValid || pair.TitleBubbleId.IsNull) continue;
                                    BlockReference bRef = transaction.GetObject(pair.TitleBubbleId, OpenMode.ForRead, false, true) as BlockReference;
                                    if (bRef != null && !bRef.IsErased)
                                    {
                                        validBubbles.Add(pair);
                                        string vn = CalloutHelpers.GetAttributeValue(transaction, bRef, "VIEWNUMBER");
                                        if (!string.IsNullOrEmpty(vn) && vn.StartsWith("CT", System.StringComparison.OrdinalIgnoreCase)) 
                                        {
                                            if (int.TryParse(vn.Substring(2), out int num)) 

                                            {
                                                usedNumbers.Add(num);
                                            }
                                        }
                                    }
                                }
                                mappings[sourceBlockId] = validBubbles;

                                while (usedNumbers.Contains(tempCounter))
                                {
                                    tempCounter++;
                                }
                            }
                            viewNumberValue = $"CT{tempCounter:D2}";
                        }

                        sourceLayerId = boundaryCurve.LayerId;
                        originalExtents = boundaryCurve.GeometricExtents;

                        basePoint = new Point3d(
                            (originalExtents.MinPoint.X + originalExtents.MaxPoint.X) / 2.0,
                            (originalExtents.MinPoint.Y + originalExtents.MaxPoint.Y) / 2.0,
                            (originalExtents.MinPoint.Z + originalExtents.MaxPoint.Z) / 2.0
                        );

                        string newBlockName = "CT_DETAIL_" + System.Guid.NewGuid().ToString("N").Substring(0, 10).ToUpper();
                        using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite))
                        using (BlockTableRecord detailBlockRecord = new BlockTableRecord { Name = newBlockName })
                        {
                            detailContentBlockId = blockTable.Add(detailBlockRecord);
                            transaction.AddNewlyCreatedDBObject(detailBlockRecord, true);

                            using (BlockReference innerBlock = sourceBlock.Clone() as BlockReference)
                            using (Curve innerCurve = boundaryCurve.Clone() as Curve)
                            {
                                innerCurve.LayerId = sourceLayerId;

                                Vector3d vectorToOrigin = basePoint.GetVectorTo(Point3d.Origin);
                                innerBlock.TransformBy(Matrix3d.Displacement(vectorToOrigin));
                                innerCurve.TransformBy(Matrix3d.Displacement(vectorToOrigin));

                                detailBlockRecord.AppendEntity(innerBlock);
                                transaction.AddNewlyCreatedDBObject(innerBlock, true);
                                
                                detailBlockRecord.AppendEntity(innerCurve);
                                transaction.AddNewlyCreatedDBObject(innerCurve, true);

                                ApplyXClip(transaction, innerBlock, innerCurve);
                            }
                        }
                    }
                    transaction.Commit();
                }

                // 3. XỬ LÝ KHÔNG GIAN BÊN NGOÀI TRANSACTION (Drag Jig)
                PromptResult jigResult = null;
                double calloutScale = 1.0;
                
                using (BlockReference jigReference = new BlockReference(basePoint, detailContentBlockId) { LayerId = sourceLayerId })
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
                        ObjectId backgroundLayerId = CalloutHelpers.EnsureLayer(database, transaction, "Nen", Color.FromRgb(150, 150, 150), "Continuous", LineWeight.LineWeight005);
                        ObjectId dimensionLayerId = CalloutHelpers.EnsureLayer(database, transaction, "ABC_A_Kichthuoc", Color.FromColorIndex(ColorMethod.ByAci, 8), "Continuous", LineWeight.LineWeight009);

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
                                ObjectId detailBlockId = CalloutHelpers.EnsureDetailCalloutBlock(database, transaction);
                                if (!detailBlockId.IsNull)
                                {
                                    using (Curve originalBoundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve)
                                    {
                                        Point3d closestPoint = originalBoundaryCurve.GetClosestPointTo(sourceBubblePos, false);
                                        
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

                                        using (Entity dot = CalloutHelpers.CreateLeaderDot(closestPoint, 2.0 * dimensionScale))
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
                                        _newSourceBubbleId = sourceBubble.ObjectId;
                                        
                                        var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
                                            { "VIEWNUMBER", viewNumberValue },
                                            { "SHEETNUMBER", "" }
                                        };
                                        CalloutHelpers.ApplyDictionaryAttributes(transaction, sourceBubble, dict);
                                    }
                                }
                                else
                                {
                                    editor.WriteMessage("\n[Cảnh báo]: Không thể tạo Block '_DetailCallout - Metric'!");
                                }
                            }

                            string styleName = $"TB_ABC_DIM 1-{dimensionScale:0.##}";
                            ObjectId dimensionStyleId = CalloutHelpers.EnsureCalloutDimStyle(database, transaction, dimensionScale, styleName);

                            using (BlockReference originalSourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference)
                            using (Curve originalBoundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve)
                            {
                                double dimCollisionOffset = CreateAutoDimensionGeometry(
                                    originalSourceBlock, originalBoundaryCurve, mathTransform,
                                    currentSpace, dimensionStyleId, dimensionScale, calloutScale,
                                    basePoint, finalPosition, backgroundLayerId, dimensionLayerId, transaction
                                );

                                if (isFarCallout)
                                {
                                    ObjectId detailBlockId = CalloutHelpers.EnsureDetailCalloutBlock(database, transaction);
                                    if (!detailBlockId.IsNull)
                                    {
                                        Extents3d finalExtents;
                                        using (Curve clonedBoundary = originalBoundaryCurve.Clone() as Curve)
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
                                            textTitle.TextStyleId = CalloutHelpers.EnsureTextStyle(database, transaction, "Standard", "arial.ttf");
                                            
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
                                            CalloutHelpers.ApplyDictionaryAttributes(transaction, titleBubble, dict);
                                            _newTitleBubbleId = titleBubble.ObjectId;
                                        }
                                    }
                                }
                            }
                        }
                        transaction.Commit();
                    }

                    if (isFarCallout && !_newTitleBubbleId.IsNull && !_newSourceBubbleId.IsNull)
                    {
                        var mappings = GetCalloutMappings(database);
                        if (!mappings.ContainsKey(sourceBlockId))
                            mappings[sourceBlockId] = new List<CalloutBubblePair>();
                        mappings[sourceBlockId].Add(new CalloutBubblePair { SourceBubbleId = _newSourceBubbleId, TitleBubbleId = _newTitleBubbleId });
                    }
                }
            }
            catch (System.Exception ex)
            {
                // NGUYÊN TẮC: Ghi lỗi trực quan, triệt để thay vì im lặng
                editor.WriteMessage($"\n[ERROR Lỗi Hệ Thống]: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        private static void ExtractGeometryRecursive(ObjectId blockRecordId, Matrix3d matrix, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints, List<Arc> validArcs, Transaction transaction)
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

        private static double CreateAutoDimensionGeometry(BlockReference sourceBlock, Curve boundaryCurve, Matrix3d finalTransform, BlockTableRecord currentSpace, ObjectId dimensionStyleId, double dimensionScale, double calloutScale, Point3d originalCenter, Point3d newCenter, ObjectId backgroundLayerId, ObjectId dimensionLayerId, Transaction transaction)
        {
            List<Point3d> validPoints = new List<Point3d>();
            List<Arc> validArcs = new List<Arc>();
            ObjectIdCollection backgroundObjectIds = new ObjectIdCollection();
            double extraBottomOffset = 0.0;

            try
            {
                Extents3d clipBox = boundaryCurve.GeometricExtents;
                ExtractGeometryRecursive(sourceBlock.BlockTableRecord, sourceBlock.BlockTransform, clipBox, finalTransform, validPoints, validArcs, transaction);

                Extents3d finalBoundaryExtents;
                using (Curve clonedBoundary = boundaryCurve.Clone() as Curve)
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
                        CalloutHelpers.SetFixedExtensionLine(arcDimension, 6.0);

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
                            CalloutHelpers.SetFixedExtensionLine(dimensionX, 6.0);

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
                            CalloutHelpers.SetFixedExtensionLine(totalDimensionX, 6.0);

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
                            CalloutHelpers.SetFixedExtensionLine(dimensionY, 6.0);

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
                            CalloutHelpers.SetFixedExtensionLine(totalDimensionY, 6.0);

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

        private static bool IsInsideClip(Point3d pointWcs, Extents3d clipBox)
        {
            return pointWcs.X >= clipBox.MinPoint.X && pointWcs.X <= clipBox.MaxPoint.X &&
                   pointWcs.Y >= clipBox.MinPoint.Y && pointWcs.Y <= clipBox.MaxPoint.Y;
        }

        private static void AddPointIfInsideClip(Point3d pointWcs, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints)
        {
            if (IsInsideClip(pointWcs, clipBox))
            {
                validPoints.Add(pointWcs.TransformBy(finalTransform));
            }
        }

        private static void ApplyXClip(Transaction transaction, BlockReference blockReference, Curve boundaryCurve)
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
                    
                    if (boundaryCurve is Polyline poly && !poly.HasBulges)
                    {
                        for (int i = 0; i < poly.NumberOfVertices; i++)
                        {
                            Point3d pointInBlock = poly.GetPoint3dAt(i).TransformBy(worldToBlockMatrix);
                            points.Add(new Point2d(pointInBlock.X, pointInBlock.Y));
                        }
                    }
                    else
                    {
                        double startParam = boundaryCurve.StartParam;
                        double endParam = boundaryCurve.EndParam;
                        int numSegments = 72; // Create a smooth 72-segment polygon approximation
                        for (int i = 0; i < numSegments; i++)
                        {
                            double t = (double)i / numSegments;
                            double param = startParam + (endParam - startParam) * t;
                            Point3d pt = boundaryCurve.GetPointAtParameter(param).TransformBy(worldToBlockMatrix);
                            points.Add(new Point2d(pt.X, pt.Y));
                        }
                        
                        // Close the loop explicitly just in case for the filter definition
                        Point3d firstPt = boundaryCurve.GetPointAtParameter(startParam).TransformBy(worldToBlockMatrix);
                        points.Add(new Point2d(firstPt.X, firstPt.Y));
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
    }
}

