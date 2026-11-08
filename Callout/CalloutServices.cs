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
        private static readonly HashSet<Database> _pendingLeaderDatabases = new HashSet<Database>();

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
            _pendingLeaderDatabases.Clear();
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
                _pendingLeaderDatabases.Remove(e.Document.Database);
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
            doc.Database.ObjectReappended += Database_ObjectModified;
            doc.CommandEnded += Document_CommandFinished;
            doc.CommandCancelled += Document_CommandFinished;
            doc.CommandFailed += Document_CommandFinished;
            _pendingLeaderDatabases.Add(doc.Database);
        }

        private static void DetachFromDocument(Document doc)
        {
            doc.Database.ObjectAppended -= Database_ObjectModified;
            doc.Database.ObjectModified -= Database_ObjectModified;
            doc.Database.ObjectReappended -= Database_ObjectModified;
            doc.CommandEnded -= Document_CommandFinished;
            doc.CommandCancelled -= Document_CommandFinished;
            doc.CommandFailed -= Document_CommandFinished;
        }

        private static void Database_ObjectModified(object sender, ObjectEventArgs e)
        {
            try
            {
                if (_isUpdating) return;

                if (e.DBObject is Polyline)
                {
                    Database leaderDatabase = sender as Database;
                    if (leaderDatabase != null && !leaderDatabase.IsDisposed) _pendingLeaderDatabases.Add(leaderDatabase);
                    return;
                }

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
                if (doc == null || !doc.Editor.IsQuiescent)
                {
                    return;
                }

                SynchronizePendingLeaders(doc);
                if (!_pendingDatabases.Contains(doc.Database)) return;

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

        private static void Document_CommandFinished(object sender, CommandEventArgs e)
        {
            if (_isUpdating) return;
            try
            {
                Document document = sender as Document;
                if (document == null || document.Database.IsDisposed) return;
                string command = e.GlobalCommandName;
                if (string.Equals(command, "U", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(command, "UNDO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(command, "REDO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(command, "MREDO", StringComparison.OrdinalIgnoreCase))
                {
                    // Dependent changes made at CommandEnded belong to the
                    // original command's undo record. Do not start another
                    // write transaction after U/REDO and invalidate its redo chain.
                    _pendingLeaderDatabases.Remove(document.Database);
                    return;
                }
                SynchronizePendingLeaders(document);
            }
            catch (Exception ex)
            {
                WriteError("Leader command completed", ex);
            }
        }

        private static void SynchronizePendingLeaders(Document document)
        {
            if (_isUpdating || !_pendingLeaderDatabases.Contains(document.Database)) return;
            _isUpdating = true;
            try
            {
                // Command callbacks and Idle both run on AutoCAD's main thread.
                // Never modify dependent entities in ObjectModified itself.
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Callout.Logic.CalloutLeaderLinks.Synchronize(document.Database, transaction);
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                WriteError("Synchronize leader endpoints", ex);
            }
            finally
            {
                _pendingLeaderDatabases.Remove(document.Database);
                _isUpdating = false;
            }
        }

        private static void UpdateCalloutsPositional(Document doc)
        {
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var mappings = Callout.Logic.CalloutCoreLogic.GetCalloutMappings(doc.Database, tr);
                var titleBlocksBySpace = new Dictionary<ObjectId, List<Tuple<Extents3d, string, double?>>>();
                foreach (var pairs in mappings.Values)
                {
                    foreach (var pair in pairs)
                    {
                        BlockReference titleBubble = (BlockReference)tr.GetObject(pair.TitleBubbleId, OpenMode.ForRead);
                        BlockReference sourceBubble = pair.SourceBubbleId.IsNull ? null :
                            tr.GetObject(pair.SourceBubbleId, OpenMode.ForRead) as BlockReference;
                        var target = FindTitleBlock(tr, titleBubble, titleBlocksBySpace);
                        if (target != null)
                        {
                            UpdateSheet(tr, titleBubble, target.Item2);
                            if (sourceBubble != null) UpdateSheet(tr, sourceBubble, target.Item2);
                            UpdateScale(titleBubble, target.Item3);
                        }

                        if (sourceBubble != null)
                        {
                            var source = FindTitleBlock(tr, sourceBubble, titleBlocksBySpace);
                            if (source != null) UpdateScale(sourceBubble, source.Item3);
                        }
                    }
                }
                tr.Commit();
            }
        }

        private static Tuple<Extents3d, string, double?> FindTitleBlock(
            Transaction transaction,
            BlockReference bubble,
            Dictionary<ObjectId, List<Tuple<Extents3d, string, double?>>> titleBlocksBySpace)
        {
            // Each space has an independent coordinate system. Never compare a
            // ModelSpace bubble with a title block in the active paper layout.
            if (!titleBlocksBySpace.TryGetValue(bubble.OwnerId, out var titleBlocks))
            {
                titleBlocks = CalloutHelpers.ReadTitleBlocks(transaction, bubble.OwnerId);
                titleBlocksBySpace.Add(bubble.OwnerId, titleBlocks);
            }

            Point3d position = bubble.Position;
            foreach (var titleBlock in titleBlocks)
            {
                Extents3d extents = titleBlock.Item1;
                if (position.X >= extents.MinPoint.X && position.X <= extents.MaxPoint.X &&
                    position.Y >= extents.MinPoint.Y && position.Y <= extents.MaxPoint.Y) return titleBlock;
            }
            return null;
        }

        private static void UpdateSheet(Transaction transaction, BlockReference bubble, string sheet)
        {
            if (sheet == null || CalloutHelpers.GetAttributeValue(transaction, bubble, "SHEETNUMBER") == sheet) return;
            if (!bubble.IsWriteEnabled) bubble.UpgradeOpen();
            CalloutHelpers.SetAttributeValue(transaction, bubble, "SHEETNUMBER", sheet);
        }

        private static void UpdateScale(BlockReference bubble, double? targetScale)
        {
            if (!targetScale.HasValue) return;
            double currentScale = Math.Abs(bubble.ScaleFactors.X);
            if (currentScale <= 0.0 || Math.Abs(currentScale - targetScale.Value) <= 0.001) return;
            if (!bubble.IsWriteEnabled) bubble.UpgradeOpen();
            // TransformBy also transforms the attached attributes, preserving
            // mirrored signs, insertion point and existing proportions.
            bubble.TransformBy(Matrix3d.Scaling(targetScale.Value / currentScale, bubble.Position));
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
