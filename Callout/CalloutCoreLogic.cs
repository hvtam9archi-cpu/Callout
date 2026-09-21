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
        private const string FilterDictionaryName = "ACAD_FILTER";
        private const string SpatialFilterName = "SPATIAL";
        private const double ClipPlanarityTolerance = 1.0e-6;

        private static readonly Dictionary<Database, Dictionary<ObjectId, List<CalloutBubblePair>>> _blockCalloutBubbles = new Dictionary<Database, Dictionary<ObjectId, List<CalloutBubblePair>>>();
        private static bool _documentDestroyedHandlerAttached;

        private static Callout.UI.CalloutManagerWindow _managerWindow = null;

        // Cleanup disposed Database entries khi bản vẽ đóng — tránh memory leak static dict
        static CalloutCoreLogic()
        {
            try
            {
                Application.DocumentManager.DocumentDestroyed += DocumentManager_DocumentDestroyed;
                _documentDestroyedHandlerAttached = true;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout] Không đăng ký DocumentDestroyed: {ex}");
            }
        }

        public static void Terminate()
        {
            if (!_documentDestroyedHandlerAttached) return;

            try
            {
                Application.DocumentManager.DocumentDestroyed -= DocumentManager_DocumentDestroyed;
                _documentDestroyedHandlerAttached = false;
                _blockCalloutBubbles.Clear();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout] Không hủy DocumentDestroyed: {ex}");
            }
        }

        private static void DocumentManager_DocumentDestroyed(object sender, DocumentDestroyedEventArgs e)
        {
            try
            {
                var keysToRemove = new List<Database>();
                foreach (var key in _blockCalloutBubbles.Keys)
                {
                    if (key == null || key.IsDisposed)
                    {
                        keysToRemove.Add(key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _blockCalloutBubbles.Remove(key);
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout] Dọn mapping khi đóng document lỗi: {ex}");
            }
        }

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
                if (_managerWindow == null || !_managerWindow.IsLoaded)
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

        public static void RepairCalloutCrops()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            int spatialFilterCount = 0;
            int repairedFilterCount = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    foreach (ObjectId blockRecordId in blockTable)
                    {
                        BlockTableRecord blockRecord = transaction.GetObject(blockRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (blockRecord == null || blockRecord.IsFromExternalReference) continue;

                        foreach (ObjectId entityId in blockRecord)
                        {
                            if (entityId.IsErased) continue;

                            BlockReference blockReference = transaction.GetObject(entityId, OpenMode.ForRead, false, true) as BlockReference;
                            if (blockReference == null) continue;

                            if (EnsureSpatialFilterCloneable(transaction, blockReference, out bool changed))
                            {
                                spatialFilterCount++;
                                if (changed) repairedFilterCount++;
                            }
                        }
                    }

                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    $"\n[Callout] Crop repair complete: {repairedFilterCount}/{spatialFilterCount} spatial filters updated for cross-drawing copy.");
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage($"\n[Callout] Crop repair failed: {ex.Message}");
            }
        }

        public static void ExecuteCallout(bool isFarCallout)
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Database database = document.Database;
            Editor editor = document.Editor;
            CalloutJig calloutJig = null;

            // NGUYÊN TẮC: Crash-proof Safety (Global try-catch tại Entry Point)
            UndoHelper.Begin(document);
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

                ObjectId sourceLayerId = ObjectId.Null;
                Extents3d originalExtents = new Extents3d();
                Point3d basePoint = Point3d.Origin;

                // 2. TRANSACTION ĐỢT 1: Lấy thông tin và tạo các entity preview trong bộ nhớ.
                using (DocumentLock docLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockReference sourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference;
                    Curve boundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve;
                    {
                        if (sourceBlock == null || boundaryCurve == null) return;
                        
                        if (!boundaryCurve.Closed)
                        {
                            editor.WriteMessage("\nKhung trích phải là đường cong khép kín.");
                            return;
                        }

                        if (!TryGetPlanarElevation(boundaryCurve, Matrix3d.Identity, out _) ||
                            !TryGetPlanarElevation(boundaryCurve, sourceBlock.BlockTransform.Inverse(), out _))
                        {
                            editor.WriteMessage("\nKhung trích phải nằm trên mặt phẳng 2D song song XY của bản vẽ và của block.");
                            return;
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

                        calloutJig = CreateCalloutJig(
                            transaction,
                            sourceBlock,
                            boundaryCurve,
                            basePoint,
                            originalExtents,
                            isFarCallout);
                    }
                    transaction.Commit();
                }

                // 3. XỬ LÝ KHÔNG GIAN BÊN NGOÀI TRANSACTION (Drag Jig)
                PromptResult jigResult = null;
                double calloutScale = 1.0;
                
                if (calloutJig == null) return;
                using (calloutJig)
                {
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

                    Point3d finalPosition = calloutJig.CurrentPosition;
                    double? detailAutoScale;
                    double? sourceAutoScale;
                    using (Transaction tr2 = database.TransactionManager.StartTransaction())
                    {
                        double?[] detectedScales = CalloutHelpers.GetScalesFromTitleBlocksAtPositions(
                            database,
                            tr2,
                            finalPosition,
                            basePoint);
                        detailAutoScale = detectedScales[0];
                        sourceAutoScale = detectedScales[1];
                        tr2.Commit();
                    }

                    double detailDimensionScale;
                    if (detailAutoScale.HasValue)
                    {
                        detailDimensionScale = detailAutoScale.Value;
                        editor.WriteMessage($"\nĐã tự động lấy Dimscale chi tiết trích từ khung bản vẽ: {detailDimensionScale}");
                    }
                    else
                    {
                        PromptDoubleOptions promptDimScaleOptions = new PromptDoubleOptions("\nDimscale chi tiết trích: ")
                        {
                            AllowZero = false,
                            AllowNegative = false,
                            DefaultValue = calloutScale,
                            UseDefaultValue = true
                        };

                        PromptDoubleResult promptDimScaleResult = editor.GetDouble(promptDimScaleOptions);
                        if (promptDimScaleResult.Status != PromptStatus.OK) return;

                        detailDimensionScale = promptDimScaleResult.Value;
                    }

                    double sourceDimensionScale = sourceAutoScale ?? detailDimensionScale;
                    if (sourceAutoScale.HasValue) 
                    {
                         editor.WriteMessage($"\nĐã tự động lấy Dimscale đối tượng gốc từ khung bản vẽ: {sourceDimensionScale}");
                    }

                    double dimensionScale = detailDimensionScale;
                    Matrix3d mathTransform = Matrix3d.Scaling(calloutScale, finalPosition) *
                                             Matrix3d.Displacement(basePoint.GetVectorTo(finalPosition));

                    Point3d sourceBubblePos = basePoint;
                    if (isFarCallout)
                    {
                        sourceBubblePos = new Point3d(originalExtents.MaxPoint.X + (12.0 * sourceDimensionScale), originalExtents.MaxPoint.Y + (12.0 * sourceDimensionScale), 0);
                    }

                    // 4. TRANSACTION ĐỢT 2: Deep-clone nội dung trực tiếp, gắn XClip và thêm annotations.
                    using (DocumentLock docLock = document.LockDocument())
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        ObjectId dimensionLayerId = CalloutHelpers.EnsureLayer(database, transaction, "ABC_A_Kichthuoc", Color.FromColorIndex(ColorMethod.ByAci, 8), "Continuous", LineWeight.LineWeight009);
                        ObjectId clonedBlockId = DeepCloneBlockReference(database, sourceBlockId, database.CurrentSpaceId);

                        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                        {
                            BlockReference detailBlock = transaction.GetObject(clonedBlockId, OpenMode.ForWrite, false, true) as BlockReference;
                            Curve originalBoundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve;
                            if (detailBlock == null || originalBoundaryCurve == null)
                            {
                                throw new InvalidOperationException("Không thể tạo đối tượng chi tiết trích.");
                            }

                            detailBlock.TransformBy(mathTransform);

                            Polyline detailBoundary = CreateBoundaryPolyline(
                                originalBoundaryCurve,
                                mathTransform,
                                sourceLayerId);
                            bool boundaryAdded = false;
                            try
                            {
                                currentSpace.AppendEntity(detailBoundary);
                                transaction.AddNewlyCreatedDBObject(detailBoundary, true);
                                boundaryAdded = true;
                                ApplyXClip(transaction, detailBlock, detailBoundary);
                            }
                            finally
                            {
                                if (!boundaryAdded && detailBoundary != null && !detailBoundary.IsDisposed)
                                {
                                    detailBoundary.Dispose();
                                }
                            }

                            if (!isFarCallout)
                            {
                                double dotDiameter = 1.5 * sourceDimensionScale;
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
                                    Curve farBoundaryCurve = transaction.GetObject(boundaryPolylineId, OpenMode.ForRead) as Curve;
                                    {
                                        Point3d closestPoint = farBoundaryCurve.GetClosestPointTo(sourceBubblePos, false);
                                        
                                        double bubbleRadius = 6.0 * sourceDimensionScale;
                                        Point3d leaderEnd = new Point3d(sourceBubblePos.X - bubbleRadius, sourceBubblePos.Y, 0);
                                        double kneeX = Math.Max(leaderEnd.X - (4.0 * sourceDimensionScale), closestPoint.X + (2.0 * sourceDimensionScale));
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

                                        using (Entity dot = CalloutHelpers.CreateLeaderDot(closestPoint, 2.0 * sourceDimensionScale))
                                        {
                                            dot.LayerId = sourceLayerId;
                                            currentSpace.AppendEntity(dot);
                                            transaction.AddNewlyCreatedDBObject(dot, true);
                                        }
                                    }

                                    using (BlockReference sourceBubble = new BlockReference(sourceBubblePos, detailBlockId))
                                    {
                                        sourceBubble.ScaleFactors = new Scale3d(sourceDimensionScale);
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

                            BlockReference originalSourceBlock = transaction.GetObject(sourceBlockId, OpenMode.ForRead) as BlockReference;
                            {
                                double dimCollisionOffset = CreateAutoDimensionGeometry(
                                    originalSourceBlock, originalBoundaryCurve, mathTransform,
                                    currentSpace, dimensionStyleId, dimensionScale, calloutScale,
                                    basePoint, finalPosition, dimensionLayerId, transaction
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
                                             }
                                             catch (System.Exception ex)
                                             {
                                                 System.Diagnostics.Debug.WriteLine($"[Callout] Không đọc được bounds DBText: {ex}");
                                             }
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
            finally
            {
                if (calloutJig != null)
                {
                    calloutJig.Dispose();
                }
                UndoHelper.End(document);
            }
        }

        private static CalloutJig CreateCalloutJig(
            Transaction transaction,
            BlockReference sourceBlock,
            Curve boundaryCurve,
            Point3d basePoint,
            Extents3d originalExtents,
            bool isFarCallout)
        {
            BlockReference previewBlock = null;
            Curve previewBoundary = null;
            List<AttributeReference> previewAttributes = new List<AttributeReference>();

            try
            {
                previewBlock = sourceBlock.Clone() as BlockReference;
                previewBoundary = boundaryCurve.Clone() as Curve;
                if (previewBlock == null || previewBoundary == null)
                {
                    throw new InvalidOperationException("Không thể tạo dữ liệu xem trước cho chi tiết trích.");
                }

                foreach (ObjectId attributeId in sourceBlock.AttributeCollection)
                {
                    if (attributeId.IsNull || attributeId.IsErased) continue;

                    AttributeReference sourceAttribute = transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                    AttributeReference previewAttribute = sourceAttribute?.Clone() as AttributeReference;
                    if (previewAttribute != null)
                    {
                        previewAttributes.Add(previewAttribute);
                    }
                }

                Point2dCollection clipBoundaryPoints = CreateClipBoundaryPoints(boundaryCurve, Matrix3d.Identity);
                CalloutJig jig = new CalloutJig(
                    previewBlock,
                    previewBoundary,
                    previewAttributes,
                    clipBoundaryPoints,
                    basePoint,
                    originalExtents,
                    isFarCallout);

                previewBlock = null;
                previewBoundary = null;
                previewAttributes = null;
                return jig;
            }
            finally
            {
                if (previewBlock != null && !previewBlock.IsDisposed)
                {
                    previewBlock.Dispose();
                }

                if (previewBoundary != null && !previewBoundary.IsDisposed)
                {
                    previewBoundary.Dispose();
                }

                if (previewAttributes != null)
                {
                    foreach (AttributeReference attribute in previewAttributes)
                    {
                        if (attribute != null && !attribute.IsDisposed)
                        {
                            attribute.Dispose();
                        }
                    }
                }
            }
        }

        private static ObjectId DeepCloneBlockReference(Database database, ObjectId sourceId, ObjectId ownerId)
        {
            if (sourceId.IsNull || !sourceId.IsValid || sourceId.IsErased)
            {
                throw new ArgumentException("ObjectId nguồn không hợp lệ.", nameof(sourceId));
            }

            ObjectIdCollection sourceIds = new ObjectIdCollection();
            sourceIds.Add(sourceId);

            using (IdMapping idMapping = new IdMapping())
            {
                database.DeepCloneObjects(sourceIds, ownerId, idMapping, false);
                IdPair idPair = idMapping.Lookup(sourceId);
                if (!idPair.IsCloned || idPair.Value.IsNull || !idPair.Value.IsValid)
                {
                    throw new InvalidOperationException("AutoCAD không trả về đối tượng đã deep-clone.");
                }

                return idPair.Value;
            }
        }

        private static Polyline CreateBoundaryPolyline(
            Curve sourceBoundary,
            Matrix3d transform,
            ObjectId layerId)
        {
            List<Point3d> samplePoints = CreateBoundarySamplePoints(sourceBoundary, transform);
            if (!TryGetPlanarElevation(samplePoints, out double elevation))
            {
                throw new InvalidOperationException("Khung trích không còn đồng phẳng sau khi biến đổi.");
            }

            Point2dCollection points = CreateClipBoundaryPoints(samplePoints);
            Polyline boundary = new Polyline(points.Count);

            try
            {
                boundary.SetDatabaseDefaults();
                boundary.SetPropertiesFrom(sourceBoundary);
                boundary.LayerId = layerId;
                boundary.Elevation = elevation;

                for (int i = 0; i < points.Count; i++)
                {
                    boundary.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
                }

                boundary.Closed = true;
                return boundary;
            }
            catch
            {
                boundary.Dispose();
                throw;
            }
        }

        private static Point2dCollection CreateClipBoundaryPoints(Curve boundaryCurve, Matrix3d transform)
        {
            return CreateClipBoundaryPoints(CreateBoundarySamplePoints(boundaryCurve, transform));
        }

        private static List<Point3d> CreateBoundarySamplePoints(Curve boundaryCurve, Matrix3d transform)
        {
            List<Point3d> points = new List<Point3d>();

            if (boundaryCurve is Polyline polyline && !polyline.HasBulges)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    points.Add(polyline.GetPoint3dAt(i).TransformBy(transform));
                }
            }
            else
            {
                double startParameter = boundaryCurve.StartParam;
                double endParameter = boundaryCurve.EndParam;
                const int segmentCount = 72;

                for (int i = 0; i < segmentCount; i++)
                {
                    double ratio = (double)i / segmentCount;
                    double parameter = startParameter + ((endParameter - startParameter) * ratio);
                    points.Add(boundaryCurve.GetPointAtParameter(parameter).TransformBy(transform));
                }
            }

            if (points.Count < 3)
            {
                throw new InvalidOperationException("Khung trích phải tạo được ít nhất ba điểm clip.");
            }

            return points;
        }

        private static Point2dCollection CreateClipBoundaryPoints(IReadOnlyList<Point3d> samplePoints)
        {
            Point2dCollection points = new Point2dCollection();
            for (int i = 0; i < samplePoints.Count; i++)
            {
                Point3d point = samplePoints[i];
                points.Add(new Point2d(point.X, point.Y));
            }

            return points;
        }

        private static bool TryGetPlanarElevation(Curve boundaryCurve, Matrix3d transform, out double elevation)
        {
            return TryGetPlanarElevation(CreateBoundarySamplePoints(boundaryCurve, transform), out elevation);
        }

        private static bool TryGetPlanarElevation(IReadOnlyList<Point3d> samplePoints, out double elevation)
        {
            elevation = samplePoints.Count > 0 ? samplePoints[0].Z : 0.0;
            for (int i = 1; i < samplePoints.Count; i++)
            {
                if (Math.Abs(samplePoints[i].Z - elevation) > ClipPlanarityTolerance)
                {
                    return false;
                }
            }

            return samplePoints.Count >= 3;
        }

        private static void ExtractGeometryRecursive(ObjectId blockRecordId, Matrix3d matrix, Extents3d clipBox, Matrix3d finalTransform, List<Point3d> validPoints, List<Arc> validArcs, List<Circle> validCircles, Transaction transaction, HashSet<ObjectId> activeBlockRecords)
        {
            if (blockRecordId.IsNull || blockRecordId.IsErased || !activeBlockRecords.Add(blockRecordId))
            {
                return;
            }

            try
            {
                BlockTableRecord blockRecord = (BlockTableRecord)transaction.GetObject(blockRecordId, OpenMode.ForRead);
                foreach (ObjectId entityId in blockRecord)
                {
                    Entity entity = transaction.GetObject(entityId, OpenMode.ForRead) as Entity;
                    if (entity == null || !entity.Visible) continue;

                    if (entity is BlockReference blockReference)
                    {
                        Matrix3d newMatrix = matrix * blockReference.BlockTransform;
                        // BlockTableRecord là definition đã evaluate của dynamic block.
                        // DynamicBlockTableRecord là authoring definition và có thể không phản ánh trạng thái hiển thị hiện tại.
                        ObjectId targetRecordId = blockReference.BlockTableRecord;
                        ExtractGeometryRecursive(targetRecordId, newMatrix, clipBox, finalTransform, validPoints, validArcs, validCircles, transaction, activeBlockRecords);
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
                        // Phát hiện Polyline tròn (2 vertices + closed + bulge ≈ 1.0 = Circle)
                        if (polyline.Closed && polyline.NumberOfVertices == 2 &&
                            Math.Abs(Math.Abs(polyline.GetBulgeAt(0)) - 1.0) < 0.01 &&
                            Math.Abs(Math.Abs(polyline.GetBulgeAt(1)) - 1.0) < 0.01)
                        {
                            try
                            {
                                CircularArc2d seg0 = polyline.GetArcSegment2dAt(0);
                                Point3d centerWcs = new Point3d(seg0.Center.X, seg0.Center.Y, 0).TransformBy(matrix);
                                double r = seg0.Radius * matrix.GetScale();
                                if (IsInsideClip(centerWcs, clipBox) ||
                                    IsInsideClip(centerWcs + new Vector3d(r, 0, 0), clipBox) ||
                                    IsInsideClip(centerWcs + new Vector3d(-r, 0, 0), clipBox))
                                {
                                    Circle syntheticCircle = null;
                                    try
                                    {
                                        syntheticCircle = new Circle(new Point3d(seg0.Center.X, seg0.Center.Y, 0), Vector3d.ZAxis, seg0.Radius);
                                        syntheticCircle.SetDatabaseDefaults();
                                        syntheticCircle.TransformBy(finalTransform * matrix);
                                        validCircles.Add(syntheticCircle);
                                        syntheticCircle = null;
                                    }
                                    finally
                                    {
                                        if (syntheticCircle != null && !syntheticCircle.IsDisposed) syntheticCircle.Dispose();
                                    }
                                }
                            }
                            catch (System.Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Callout] Không tạo được circle từ polyline: {ex}");
                            }
                        }
                        else
                        {
                            // Tách arc segments từ Polyline có bulge
                            ExtractArcsFromPolyline(polyline, matrix, clipBox, finalTransform, validArcs);
                        }
                    }
                    else if (entity is Polyline2d polyline2d)
                    {
                        foreach (ObjectId vertexId in polyline2d)
                        {
                            Vertex2d vertex = transaction.GetObject(vertexId, OpenMode.ForRead) as Vertex2d;
                            if (vertex != null) AddPointIfInsideClip(vertex.Position.TransformBy(matrix), clipBox, finalTransform, validPoints);
                        }
                    }
                    else if (entity is Circle circle)
                    {
                        Point3d centerWcs = circle.Center.TransformBy(matrix);
                        double r = circle.Radius * matrix.GetScale();
                        // Quadrant points cho linear dim
                        Point3d qRight = centerWcs + new Vector3d(r, 0, 0);
                        Point3d qLeft = centerWcs + new Vector3d(-r, 0, 0);
                        Point3d qTop = centerWcs + new Vector3d(0, r, 0);
                        Point3d qBot = centerWcs + new Vector3d(0, -r, 0);
                        AddPointIfInsideClip(qRight, clipBox, finalTransform, validPoints);
                        AddPointIfInsideClip(qLeft, clipBox, finalTransform, validPoints);
                        AddPointIfInsideClip(qTop, clipBox, finalTransform, validPoints);
                        AddPointIfInsideClip(qBot, clipBox, finalTransform, validPoints);

                        // Thu thập Circle - kiểm tra bất kỳ phần nào nằm trong clip box
                        if (IsInsideClip(centerWcs, clipBox) || IsInsideClip(qRight, clipBox) ||
                            IsInsideClip(qLeft, clipBox) || IsInsideClip(qTop, clipBox) || IsInsideClip(qBot, clipBox))
                        {
                            Circle clonedCircle = null;
                            try
                            {
                                clonedCircle = circle.Clone() as Circle;
                                clonedCircle.TransformBy(finalTransform * matrix);
                                validCircles.Add(clonedCircle);
                                clonedCircle = null;
                            }
                            finally
                            {
                                if (clonedCircle != null && !clonedCircle.IsDisposed) clonedCircle.Dispose();
                            }
                        }
                    }
                    else if (entity is Arc arc)
                    {
                        Point3d startWcs = arc.StartPoint.TransformBy(matrix);
                        Point3d endWcs = arc.EndPoint.TransformBy(matrix);
                        Point3d midPointWcs = arc.GetPointAtParameter(arc.StartParam + (arc.EndParam - arc.StartParam) / 2.0).TransformBy(matrix);
                        // Chấp nhận nếu bất kỳ điểm nào (start, mid, end) nằm trong clip box
                        if (IsInsideClip(midPointWcs, clipBox) || IsInsideClip(startWcs, clipBox) || IsInsideClip(endWcs, clipBox))
                        {
                            Arc clonedArc = null;
                            try
                            {
                                clonedArc = arc.Clone() as Arc;
                                clonedArc.TransformBy(finalTransform * matrix);
                                validArcs.Add(clonedArc);
                                clonedArc = null;
                            }
                            finally
                            {
                                if (clonedArc != null && !clonedArc.IsDisposed) clonedArc.Dispose();
                            }
                        }
                        // Start/End points cho linear dim
                        AddPointIfInsideClip(startWcs, clipBox, finalTransform, validPoints);
                        AddPointIfInsideClip(endWcs, clipBox, finalTransform, validPoints);
                    }
                    else if (entity is Ellipse ellipse)
                    {
                        // Lấy bounding box quadrant points của Ellipse cho linear dim
                        Point3d centerWcs = ellipse.Center.TransformBy(matrix);
                        if (IsInsideClip(centerWcs, clipBox))
                        {
                            try
                            {
                                Extents3d ellipseExtents = ellipse.GeometricExtents;
                                Point3d eMin = ellipseExtents.MinPoint.TransformBy(matrix);
                                Point3d eMax = ellipseExtents.MaxPoint.TransformBy(matrix);
                                AddPointIfInsideClip(new Point3d(eMin.X, centerWcs.Y, 0), clipBox, finalTransform, validPoints);
                                AddPointIfInsideClip(new Point3d(eMax.X, centerWcs.Y, 0), clipBox, finalTransform, validPoints);
                                AddPointIfInsideClip(new Point3d(centerWcs.X, eMin.Y, 0), clipBox, finalTransform, validPoints);
                                AddPointIfInsideClip(new Point3d(centerWcs.X, eMax.Y, 0), clipBox, finalTransform, validPoints);
                            }
                            catch (System.Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Callout] Không đọc được bounds ellipse: {ex}");
                            }
                        }
                    }
                }
            }
            finally
            {
                activeBlockRecords.Remove(blockRecordId);
            }
        }

        private static double CreateAutoDimensionGeometry(BlockReference sourceBlock, Curve boundaryCurve, Matrix3d finalTransform, BlockTableRecord currentSpace, ObjectId dimensionStyleId, double dimensionScale, double calloutScale, Point3d originalCenter, Point3d newCenter, ObjectId dimensionLayerId, Transaction transaction)
        {
            List<Point3d> validPoints = new List<Point3d>();
            List<Arc> validArcs = new List<Arc>();
            List<Circle> validCircles = new List<Circle>();
            double extraBottomOffset = 0.0;

            try
            {
                Extents3d clipBox = boundaryCurve.GeometricExtents;
                ExtractGeometryRecursive(sourceBlock.BlockTableRecord, sourceBlock.BlockTransform, clipBox, finalTransform, validPoints, validArcs, validCircles, transaction, new HashSet<ObjectId>());

                // Debug output
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\n[CT Debug] Points={validPoints.Count}, Arcs={validArcs.Count}, Circles={validCircles.Count}");

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

                // === DIM CUNG TRÒN (Arc): AlignedDimension với text "R<>" ===
                var arcGroups = GroupByRadius(validArcs.Select(a => new RadiusItem { Radius = a.Radius, Entity = a }), dimensionScale);
                foreach (var group in arcGroups)
                {
                    Arc representative = (Arc)group.First().Entity;
                    int count = group.Count;

                    try
                    {
                        // Tính điểm trên cung ở giữa
                        double midAngle = representative.StartAngle + (representative.TotalAngle / 2.0);
                        Point3d pointOnArc = new Point3d(
                            representative.Center.X + representative.Radius * Math.Cos(midAngle),
                            representative.Center.Y + representative.Radius * Math.Sin(midAngle), 0);

                        // Vị trí dim line: offset ra ngoài theo hướng midAngle
                        Vector3d outDir = new Vector3d(Math.Cos(midAngle), Math.Sin(midAngle), 0);
                        Point3d dimLinePoint = pointOnArc + outDir * (firstLayerOffset * 0.4);

                        string dimText = count > 1 ? $"{count}x R<>" : "R<>";

                        using (AlignedDimension arcDim = new AlignedDimension())
                        {
                            arcDim.SetDatabaseDefaults();
                            arcDim.XLine1Point = representative.Center;
                            arcDim.XLine2Point = pointOnArc;
                            arcDim.DimLinePoint = dimLinePoint;
                            arcDim.DimensionStyle = dimensionStyleId;
                            arcDim.Dimlfac = dimensionLinearFactor;
                            arcDim.DimensionText = dimText;
                            arcDim.LayerId = dimensionLayerId;
                            CalloutHelpers.SetFixedExtensionLine(arcDim, 6.0);

                            currentSpace.AppendEntity(arcDim);
                            transaction.AddNewlyCreatedDBObject(arcDim, true);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ed?.WriteMessage($"\n[CT] Arc dim error: {ex.Message}");
                    }
                }

                // === DIM ĐƯỜNG TRÒN (Circle): AlignedDimension với text "%%C<>" ===
                var circleGroups = GroupByRadius(validCircles.Select(c => new RadiusItem { Radius = c.Radius, Entity = c }), dimensionScale);
                int circleGroupIndex = 0;
                foreach (var group in circleGroups)
                {
                    Circle representative = (Circle)group.First().Entity;
                    int count = group.Count;

                    try
                    {
                        double angle = (Math.PI / 4.0) + (circleGroupIndex * Math.PI / 6.0);
                        Point3d pointOnCircle = new Point3d(
                            representative.Center.X + representative.Radius * Math.Cos(angle),
                            representative.Center.Y + representative.Radius * Math.Sin(angle), 0);
                        Point3d oppositePoint = new Point3d(
                            representative.Center.X - representative.Radius * Math.Cos(angle),
                            representative.Center.Y - representative.Radius * Math.Sin(angle), 0);

                        Vector3d outDir = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
                        Point3d dimLinePoint = pointOnCircle + outDir * (firstLayerOffset * 0.3);

                        // %%C = ký hiệu đường kính (⌀), Dimlfac * 2 vì dim đo bán kính nhưng hiển thị đường kính
                        string dimText = count > 1 ? $"{count}x %%C<>" : "%%C<>";

                        using (AlignedDimension circleDim = new AlignedDimension())
                        {
                            circleDim.SetDatabaseDefaults();
                            circleDim.XLine1Point = oppositePoint;
                            circleDim.XLine2Point = pointOnCircle;
                            circleDim.DimLinePoint = dimLinePoint;
                            circleDim.DimensionStyle = dimensionStyleId;
                            circleDim.Dimlfac = dimensionLinearFactor;
                            circleDim.DimensionText = dimText;
                            circleDim.LayerId = dimensionLayerId;
                            CalloutHelpers.SetFixedExtensionLine(circleDim, 6.0);

                            currentSpace.AppendEntity(circleDim);
                            transaction.AddNewlyCreatedDBObject(circleDim, true);
                        }
                        circleGroupIndex++;
                    }
                    catch (System.Exception ex)
                    {
                        ed?.WriteMessage($"\n[CT] Circle dim error: {ex.Message}");
                    }
                }

                if (validPoints.Count == 0) return extraBottomOffset;

                var distinctX = validPoints.Select(p => Math.Round(p.X, 2)).Distinct().OrderBy(x => x).ToList();
                var distinctY = validPoints.Select(p => Math.Round(p.Y, 2)).Distinct().OrderBy(y => y).ToList();

                double minDimDist = 5.0 * dimensionScale;

                // Lọc thông minh: luôn giữ điểm đầu + cuối, bỏ các điểm quá gần nhau ở giữa
                distinctX = FilterDimPoints(distinctX, minDimDist);
                distinctY = FilterDimPoints(distinctY, minDimDist);

                if (!isPlaceTop)
                {
                    if (distinctX.Count > 2) extraBottomOffset = secondLayerOffset + (4.0 * dimensionScale);
                    else if (distinctX.Count > 1) extraBottomOffset = firstLayerOffset + (4.0 * dimensionScale);
                }

                if (distinctX.Count > 1)
                {
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

            }
            finally
            {
                // NGUYÊN TẮC: Clear triệt để các Entities tạm thời đã tạo trong List
                foreach (var arc in validArcs)
                {
                    if (arc != null && !arc.IsDisposed) arc.Dispose();
                }
                foreach (var circle in validCircles)
                {
                    if (circle != null && !circle.IsDisposed) circle.Dispose();
                }
            }
            
            return extraBottomOffset;
        }

        /// <summary>
        /// Tách các arc segment từ Polyline có bulge (cung tròn trong polyline).
        /// Bulge != 0 nghĩa là đoạn đó là cung tròn, cần tạo Arc để dim bán kính.
        /// </summary>
        private static void ExtractArcsFromPolyline(Polyline polyline, Matrix3d matrix, Extents3d clipBox, Matrix3d finalTransform, List<Arc> validArcs)
        {
            int vertexCount = polyline.NumberOfVertices;
            int segCount = polyline.Closed ? vertexCount : vertexCount - 1;

            for (int i = 0; i < segCount; i++)
            {
                double bulge = polyline.GetBulgeAt(i);
                if (System.Math.Abs(bulge) < 1e-6) continue; // Không phải cung tròn

                SegmentType segType = polyline.GetSegmentType(i);
                if (segType != SegmentType.Arc) continue;

                try
                {
                    CircularArc2d arcSeg = polyline.GetArcSegment2dAt(i);
                    Point2d center2d = arcSeg.Center;
                    double radius = arcSeg.Radius;

                    Point2d startPt2d = polyline.GetPoint2dAt(i);
                    Point2d endPt2d = polyline.GetPoint2dAt((i + 1) % vertexCount);

                    Point3d startPt = new Point3d(startPt2d.X, startPt2d.Y, 0);
                    Point3d endPt = new Point3d(endPt2d.X, endPt2d.Y, 0);
                    Point3d center = new Point3d(center2d.X, center2d.Y, 0);

                    double startAngle = System.Math.Atan2(startPt.Y - center.Y, startPt.X - center.X);
                    double endAngle = System.Math.Atan2(endPt.Y - center.Y, endPt.X - center.X);

                    // Bulge > 0: CCW, Bulge < 0: CW → đảo start/end
                    if (bulge < 0)
                    {
                        double temp = startAngle;
                        startAngle = endAngle;
                        endAngle = temp;
                    }

                    // Normalize angles to [0, 2π)
                    if (startAngle < 0) startAngle += 2.0 * System.Math.PI;
                    if (endAngle < 0) endAngle += 2.0 * System.Math.PI;
                    if (endAngle <= startAngle) endAngle += 2.0 * System.Math.PI;

                    Arc arc = null;
                    try
                    {
                        arc = new Arc(center, radius, startAngle, endAngle);
                        arc.SetDatabaseDefaults();

                        // Kiểm tra midpoint có nằm trong clip box
                        Point3d midWcs = arc.GetPointAtParameter(arc.StartParam + (arc.EndParam - arc.StartParam) / 2.0).TransformBy(matrix);
                        if (IsInsideClip(midWcs, clipBox))
                        {
                            arc.TransformBy(finalTransform * matrix);
                            validArcs.Add(arc);
                            arc = null;
                        }
                    }
                    finally
                    {
                        if (arc != null && !arc.IsDisposed) arc.Dispose();
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Callout] Không xử lý được arc polyline: {ex}");
                }
            }
        }

        // ==========================================
        // CÁC HÀM HELPER ĐỘC LẬP
        // ==========================================

        /// <summary>
        /// Data class để nhóm Circle/Arc theo bán kính.
        /// </summary>
        private class RadiusItem
        {
            public double Radius { get; set; }
            public DBObject Entity { get; set; }
        }

        /// <summary>
        /// Nhóm các Circle/Arc cùng bán kính (sai số 0.5 * dimScale).
        /// Trả về các nhóm, mỗi nhóm chỉ cần dim 1 đại diện kèm số lượng.
        /// </summary>
        private static List<List<RadiusItem>> GroupByRadius(IEnumerable<RadiusItem> items, double dimScale)
        {
            double tolerance = 0.5 * dimScale;
            var sorted = items.OrderBy(i => i.Radius).ToList();
            var groups = new List<List<RadiusItem>>();

            foreach (var item in sorted)
            {
                bool added = false;
                foreach (var group in groups)
                {
                    if (Math.Abs(group[0].Radius - item.Radius) <= tolerance)
                    {
                        group.Add(item);
                        added = true;
                        break;
                    }
                }
                if (!added) groups.Add(new List<RadiusItem> { item });
            }
            return groups;
        }

        /// <summary>
        /// Lọc điểm dim thông minh: luôn giữ first + last, merge các điểm quá gần nhau ở giữa.
        /// </summary>
        private static List<double> FilterDimPoints(List<double> sortedPoints, double minDist)
        {
            if (sortedPoints.Count <= 2) return sortedPoints;

            var result = new List<double> { sortedPoints.First() };
            double lastVal = sortedPoints.Last();

            for (int i = 1; i < sortedPoints.Count - 1; i++)
            {
                // Giữ điểm nếu đủ xa điểm trước VÀ đủ xa điểm cuối
                if (sortedPoints[i] - result.Last() >= minDist && lastVal - sortedPoints[i] >= minDist)
                {
                    result.Add(sortedPoints[i]);
                }
            }

            // Luôn giữ điểm cuối
            if (Math.Abs(result.Last() - lastVal) > 0.01)
            {
                result.Add(lastVal);
            }
            return result;
        }

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

        private static bool EnsureSpatialFilterCloneable(Transaction transaction, BlockReference blockReference, out bool changed)
        {
            changed = false;
            if (blockReference.ExtensionDictionary.IsNull) return false;

            DBDictionary extensionDictionary = transaction.GetObject(
                blockReference.ExtensionDictionary,
                OpenMode.ForRead,
                false,
                true) as DBDictionary;
            if (extensionDictionary == null || !extensionDictionary.Contains(FilterDictionaryName)) return false;

            DBDictionary filterDictionary = transaction.GetObject(
                extensionDictionary.GetAt(FilterDictionaryName),
                OpenMode.ForRead,
                false,
                true) as DBDictionary;
            if (filterDictionary == null || !filterDictionary.Contains(SpatialFilterName)) return false;

            if (!extensionDictionary.TreatElementsAsHard)
            {
                extensionDictionary.UpgradeOpen();
                extensionDictionary.TreatElementsAsHard = true;
                changed = true;
            }

            if (!filterDictionary.TreatElementsAsHard)
            {
                filterDictionary.UpgradeOpen();
                filterDictionary.TreatElementsAsHard = true;
                changed = true;
            }

            return true;
        }

        private static void ApplyXClip(Transaction transaction, BlockReference blockReference, Curve boundaryCurve)
        {
            if (blockReference.ExtensionDictionary.IsNull)
            {
                blockReference.CreateExtensionDictionary();
            }

            DBDictionary extensionDictionary = transaction.GetObject(
                blockReference.ExtensionDictionary,
                OpenMode.ForWrite,
                false,
                true) as DBDictionary;
            if (extensionDictionary == null)
            {
                throw new InvalidOperationException("Không thể mở extension dictionary của block chi tiết.");
            }

            extensionDictionary.TreatElementsAsHard = true;

            DBDictionary filterDictionary;
            if (extensionDictionary.Contains(FilterDictionaryName))
            {
                filterDictionary = transaction.GetObject(
                    extensionDictionary.GetAt(FilterDictionaryName),
                    OpenMode.ForWrite,
                    false,
                    true) as DBDictionary;
                if (filterDictionary == null)
                {
                    throw new InvalidOperationException("ACAD_FILTER của block chi tiết không phải dictionary hợp lệ.");
                }
            }
            else
            {
                DBDictionary newFilterDictionary = new DBDictionary { TreatElementsAsHard = true };
                try
                {
                    extensionDictionary.SetAt(FilterDictionaryName, newFilterDictionary);
                    transaction.AddNewlyCreatedDBObject(newFilterDictionary, true);
                    filterDictionary = newFilterDictionary;
                    newFilterDictionary = null;
                }
                finally
                {
                    if (newFilterDictionary != null && !newFilterDictionary.IsDisposed)
                    {
                        newFilterDictionary.Dispose();
                    }
                }
            }

            filterDictionary.TreatElementsAsHard = true;
            if (filterDictionary.Contains(SpatialFilterName))
            {
                filterDictionary.Remove(SpatialFilterName);
            }

            Matrix3d worldToBlockMatrix = blockReference.BlockTransform.Inverse();
            List<Point3d> samplePoints = CreateBoundarySamplePoints(boundaryCurve, worldToBlockMatrix);
            if (!TryGetPlanarElevation(samplePoints, out double clipElevation))
            {
                throw new InvalidOperationException("Khung XClip không đồng phẳng trong hệ tọa độ block.");
            }

            Point2dCollection points = CreateClipBoundaryPoints(samplePoints);
            SpatialFilterDefinition spatialFilterDefinition = new SpatialFilterDefinition(
                points,
                Vector3d.ZAxis,
                clipElevation,
                0.0,
                0.0,
                true);

            SpatialFilter spatialFilter = new SpatialFilter { Definition = spatialFilterDefinition };
            try
            {
                filterDictionary.SetAt(SpatialFilterName, spatialFilter);
                transaction.AddNewlyCreatedDBObject(spatialFilter, true);
                spatialFilter = null;
            }
            finally
            {
                if (spatialFilter != null && !spatialFilter.IsDisposed)
                {
                    spatialFilter.Dispose();
                }
            }
        }
    }
}

