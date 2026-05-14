using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Callout.Services
{
    public static class CalloutConfig
    {
        public static string TitleBlockName { get; set; } = "";
        public static string SheetNumberTag { get; set; } = "";
        public static string ScaleTag { get; set; } = "";
    }

    public static class CalloutWatcher
    {
        private static bool _initialized = false;
        private static bool _needsUpdate = false;
        private static bool _isUpdating = false;

        public static void Initialize()
        {
            if (_initialized) return;
            var docMgr = Application.DocumentManager;
            docMgr.DocumentCreated += DocMgr_DocumentCreated;
            foreach (Document doc in docMgr)
            {
                AttachToDocument(doc);
            }
            Application.Idle += Application_Idle;
            _initialized = true;
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            Application.Idle -= Application_Idle;
            var docMgr = Application.DocumentManager;
            docMgr.DocumentCreated -= DocMgr_DocumentCreated;
            foreach (Document doc in docMgr)
            {
                DetachFromDocument(doc);
            }
            _initialized = false;
        }

        public static void TriggerManualUpdate()
        {
            _needsUpdate = true;
        }

        private static void DocMgr_DocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            AttachToDocument(e.Document);
        }

        private static void AttachToDocument(Document doc)
        {
            doc.Database.ObjectAppended += Database_ObjectModified;
            doc.Database.ObjectModified += Database_ObjectModified;
        }

        private static void DetachFromDocument(Document doc)
        {
            doc.Database.ObjectAppended -= Database_ObjectModified;
            doc.Database.ObjectModified -= Database_ObjectModified;
        }

        private static void Database_ObjectModified(object sender, ObjectEventArgs e)
        {
            if (_isUpdating || _needsUpdate) return;

            if (e.DBObject is BlockReference || e.DBObject is AttributeReference)
            {
                _needsUpdate = true;
            }
        }

        private static void Application_Idle(object sender, EventArgs e)
        {
            if (!_needsUpdate || _isUpdating) return;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || string.IsNullOrEmpty(CalloutConfig.TitleBlockName) || string.IsNullOrEmpty(CalloutConfig.SheetNumberTag))
            {
                _needsUpdate = false;
                return;
            }

            _isUpdating = true;
            _needsUpdate = false;

            try
            {
                UpdateCalloutsPositional(doc);
            }
            catch (Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[Callout Watcher] Lỗi cập nhật: {ex.Message}");
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private static void UpdateCalloutsPositional(Document doc)
        {
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;

                List<BlockReference> titleBlocks = new List<BlockReference>();
                foreach (ObjectId id in currentSpace)
                {
                    if (id.IsErased) continue;
                    BlockReference blk = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (blk != null)
                    {
                        string bName = CalloutHelpers.GetEffectiveBlockName(tr, blk);
                        if (bName.Equals(CalloutConfig.TitleBlockName, StringComparison.OrdinalIgnoreCase))
                        {
                            titleBlocks.Add(blk);
                        }
                    }
                }

                var tbData = new List<Tuple<Extents3d, string, double?>>();
                foreach (var tb in titleBlocks)
                {
                    try
                    {
                        Extents3d ext = tb.GeometricExtents;
                        string sheetNo = CalloutHelpers.GetAttributeValue(tr, tb, CalloutConfig.SheetNumberTag);
                        
                        double? scaleVal = null;
                        if (!string.IsNullOrEmpty(CalloutConfig.ScaleTag))
                        {
                            string scaleStr = CalloutHelpers.GetAttributeValue(tr, tb, CalloutConfig.ScaleTag);
                            if (!string.IsNullOrEmpty(scaleStr))
                            {
                                string numPart = scaleStr;
                                int idx = scaleStr.IndexOf('/');
                                if (idx == -1) idx = scaleStr.IndexOf(':');
                                if (idx != -1) numPart = scaleStr.Substring(idx + 1);

                                if (double.TryParse(numPart, out double val))
                                {
                                    scaleVal = val;
                                }
                            }
                        }

                        if (sheetNo != null || scaleVal.HasValue) tbData.Add(Tuple.Create(ext, sheetNo, scaleVal));
                    }
                    catch (Exception ex)
                    {
                        Application.DocumentManager.MdiActiveDocument?.Editor
                            .WriteMessage($"\n[Callout Watcher] Không đọc được TitleBlock: {ex.Message}");
                    }
                }

                var mappings = Callout.Logic.CalloutCoreLogic.GetCalloutMappings(doc.Database);
                foreach (var kvp in mappings)
                {
                    foreach (var pair in kvp.Value)
                    {
                        if (pair.TitleBubbleId.IsErased || pair.TitleBubbleId.IsNull) continue;

                        BlockReference titleBubble = tr.GetObject(pair.TitleBubbleId, OpenMode.ForRead) as BlockReference;
                        if (titleBubble == null || titleBubble.IsErased) continue;

                        string targetSheet = "";
                        double? targetScale = null;
                        try
                        {
                            Point3d pos = titleBubble.Position;
                            foreach (var tb in tbData)
                            {
                                if (pos.X >= tb.Item1.MinPoint.X && pos.X <= tb.Item1.MaxPoint.X &&
                                    pos.Y >= tb.Item1.MinPoint.Y && pos.Y <= tb.Item1.MaxPoint.Y)
                                {
                                    targetSheet = tb.Item2;
                                    targetScale = tb.Item3;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Application.DocumentManager.MdiActiveDocument?.Editor
                                .WriteMessage($"\n[Callout Watcher] Lỗi xác định Sheet/Scale: {ex.Message}");
                        }

                        string currentSheet = CalloutHelpers.GetAttributeValue(tr, titleBubble, "SHEETNUMBER");
                        if (currentSheet != targetSheet)
                        {
                            titleBubble.UpgradeOpen();
                            CalloutHelpers.SetAttributeValue(tr, titleBubble, "SHEETNUMBER", targetSheet ?? "");
                        }

                        if (targetScale.HasValue && Math.Abs(titleBubble.ScaleFactors.X - targetScale.Value) > 0.001)
                        {
                            if (!titleBubble.IsWriteEnabled) titleBubble.UpgradeOpen();
                            titleBubble.ScaleFactors = new Scale3d(targetScale.Value);
                        }

                        if (!pair.SourceBubbleId.IsErased && !pair.SourceBubbleId.IsNull)
                        {
                            BlockReference sourceBubble = tr.GetObject(pair.SourceBubbleId, OpenMode.ForRead) as BlockReference;
                            if (sourceBubble != null && !sourceBubble.IsErased)
                            {
                                string sourceCurrentSheet = CalloutHelpers.GetAttributeValue(tr, sourceBubble, "SHEETNUMBER");
                                if (sourceCurrentSheet != targetSheet)
                                {
                                    sourceBubble.UpgradeOpen();
                                    CalloutHelpers.SetAttributeValue(tr, sourceBubble, "SHEETNUMBER", targetSheet ?? "");
                                }

                                double? sourceTargetScale = null;
                                try
                                {
                                    Point3d sourcePos = sourceBubble.Position;
                                    foreach (var tb in tbData)
                                    {
                                        if (sourcePos.X >= tb.Item1.MinPoint.X && sourcePos.X <= tb.Item1.MaxPoint.X &&
                                            sourcePos.Y >= tb.Item1.MinPoint.Y && sourcePos.Y <= tb.Item1.MaxPoint.Y)
                                        {
                                            sourceTargetScale = tb.Item3;
                                            break;
                                        }
                                    }
                                }
                                catch {}

                                if (sourceTargetScale.HasValue && Math.Abs(sourceBubble.ScaleFactors.X - sourceTargetScale.Value) > 0.001)
                                {
                                    if (!sourceBubble.IsWriteEnabled) sourceBubble.UpgradeOpen();
                                    sourceBubble.ScaleFactors = new Scale3d(sourceTargetScale.Value);
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }
        }
    }
}
