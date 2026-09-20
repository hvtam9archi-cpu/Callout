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
        private static bool _isUpdating = false;
        private static readonly HashSet<Database> _pendingDatabases = new HashSet<Database>();

        public static void Initialize()
        {
            if (_initialized) return;
            var docMgr = Application.DocumentManager;
            try
            {
                docMgr.DocumentCreated += DocMgr_DocumentCreated;
                docMgr.DocumentToBeDestroyed += DocMgr_DocumentToBeDestroyed;
                foreach (Document doc in docMgr)
                {
                    try
                    {
                        AttachToDocument(doc);
                    }
                    catch (Exception ex)
                    {
                        WriteError("Attach", ex);
                    }
                }
                Application.Idle += Application_Idle;
                _initialized = true;
            }
            catch (Exception ex)
            {
                Terminate();
                WriteError("Initialize", ex);
                throw;
            }
        }

        public static void Terminate()
        {
            Application.Idle -= Application_Idle;
            var docMgr = Application.DocumentManager;
            docMgr.DocumentCreated -= DocMgr_DocumentCreated;
            docMgr.DocumentToBeDestroyed -= DocMgr_DocumentToBeDestroyed;
            foreach (Document doc in docMgr)
            {
                try
                {
                    DetachFromDocument(doc);
                }
                catch (Exception ex)
                {
                    WriteError("Detach", ex);
                }
            }
            _pendingDatabases.Clear();
            _initialized = false;
        }

        public static void TriggerManualUpdate()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document != null && !document.Database.IsDisposed)
            {
                _pendingDatabases.Add(document.Database);
            }
        }

        private static void DocMgr_DocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                AttachToDocument(e.Document);
            }
            catch (Exception ex)
            {
                WriteError("DocumentCreated", ex);
            }
        }

        private static void DocMgr_DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                DetachFromDocument(e.Document);
                _pendingDatabases.Remove(e.Document.Database);
            }
            catch (Exception ex)
            {
                WriteError("DocumentToBeDestroyed", ex);
            }
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
            try
            {
                if (_isUpdating) return;

                if (e.DBObject is BlockReference || e.DBObject is AttributeReference)
                {
                    Database database = sender as Database;
                    if (database != null && !database.IsDisposed)
                    {
                        _pendingDatabases.Add(database);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("ObjectModified", ex);
            }
        }

        private static void Application_Idle(object sender, EventArgs e)
        {
            if (_isUpdating) return;

            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null || !_pendingDatabases.Contains(doc.Database) || !doc.Editor.IsQuiescent)
                {
                    return;
                }

                if (string.IsNullOrEmpty(CalloutConfig.TitleBlockName) || string.IsNullOrEmpty(CalloutConfig.SheetNumberTag))
                {
                    _pendingDatabases.Remove(doc.Database);
                    return;
                }

                _isUpdating = true;
                bool updateCompleted = false;
                try
                {
                    UpdateCalloutsPositional(doc);
                    updateCompleted = true;
                }
                catch (Exception ex)
                {
                    WriteError("Idle update", ex);
                }
                finally
                {
                    if (updateCompleted) _pendingDatabases.Remove(doc.Database);
                    _isUpdating = false;
                }
            }
            catch (Exception ex)
            {
                WriteError("Idle", ex);
                _isUpdating = false;
            }
        }

        private static void UpdateCalloutsPositional(Document doc)
        {
            var mappings = Callout.Logic.CalloutCoreLogic.GetCalloutMappings(doc.Database);
            if (mappings.Count == 0) return;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace == null) return;

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

                foreach (var kvp in mappings)
                {
                    foreach (var pair in kvp.Value)
                    {
                        if (pair.TitleBubbleId.IsErased || pair.TitleBubbleId.IsNull) continue;

                        BlockReference titleBubble = tr.GetObject(pair.TitleBubbleId, OpenMode.ForRead) as BlockReference;
                        if (titleBubble == null || titleBubble.IsErased) continue;

                        string targetSheet = "";
                        double? targetScale = null;
                        bool matchedTitleBlock = false;
                        try
                        {
                            Point3d pos = titleBubble.Position;
                            foreach (var tb in tbData)
                            {
                                if (pos.X >= tb.Item1.MinPoint.X && pos.X <= tb.Item1.MaxPoint.X &&
                                    pos.Y >= tb.Item1.MinPoint.Y && pos.Y <= tb.Item1.MaxPoint.Y)
                                {
                                    matchedTitleBlock = true;
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

                        // Không xóa dữ liệu hiện có khi title block không nằm trong current space.
                        if (!matchedTitleBlock) continue;

                        string currentSheet = CalloutHelpers.GetAttributeValue(tr, titleBubble, "SHEETNUMBER");
                        if (currentSheet != targetSheet)
                        {
                            titleBubble.UpgradeOpen();
                            CalloutHelpers.SetAttributeValue(tr, titleBubble, "SHEETNUMBER", targetSheet ?? "");
                        }

                        if (targetScale.HasValue && Math.Abs(titleBubble.ScaleFactors.X - targetScale.Value) > 0.001)
                        {
                            if (!titleBubble.IsWriteEnabled) titleBubble.UpgradeOpen();
                            titleBubble.ScaleFactors = PreserveScaleSigns(titleBubble.ScaleFactors, targetScale.Value);
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
                                bool matchedSourceTitleBlock = false;
                                try
                                {
                                    Point3d sourcePos = sourceBubble.Position;
                                    foreach (var tb in tbData)
                                    {
                                        if (sourcePos.X >= tb.Item1.MinPoint.X && sourcePos.X <= tb.Item1.MaxPoint.X &&
                                            sourcePos.Y >= tb.Item1.MinPoint.Y && sourcePos.Y <= tb.Item1.MaxPoint.Y)
                                        {
                                            matchedSourceTitleBlock = true;
                                            sourceTargetScale = tb.Item3;
                                            break;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    WriteError("Source scale", ex);
                                }

                                if (matchedSourceTitleBlock && sourceTargetScale.HasValue && Math.Abs(sourceBubble.ScaleFactors.X - sourceTargetScale.Value) > 0.001)
                                {
                                    if (!sourceBubble.IsWriteEnabled) sourceBubble.UpgradeOpen();
                                    sourceBubble.ScaleFactors = PreserveScaleSigns(sourceBubble.ScaleFactors, sourceTargetScale.Value);
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }
        }

        private static Scale3d PreserveScaleSigns(Scale3d currentScale, double absoluteScale)
        {
            double xSign = currentScale.X < 0.0 ? -1.0 : 1.0;
            double ySign = currentScale.Y < 0.0 ? -1.0 : 1.0;
            double zSign = currentScale.Z < 0.0 ? -1.0 : 1.0;
            return new Scale3d(absoluteScale * xSign, absoluteScale * ySign, absoluteScale * zSign);
        }

        private static void WriteError(string operation, Exception exception)
        {
            try
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[Callout Watcher - {operation}] {exception.Message}");
            }
            catch (Exception fallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout Watcher - {operation}] {exception}\nFallback: {fallbackException}");
            }
        }
    }
}
