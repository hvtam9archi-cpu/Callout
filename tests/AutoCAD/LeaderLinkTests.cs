using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Callout.Logic;
using Callout.Services;

namespace Callout.NativeTests
{
    public class LeaderLinkTests
    {
        private static readonly Type Links = typeof(CalloutCoreLogic).Assembly.GetType("Callout.Logic.CalloutLeaderLinks", true);
        private static readonly MethodInfo Attach = Links.GetMethod("Attach");
        private static readonly MethodInfo Synchronize = Links.GetMethod("Synchronize");
        private static ObjectId _interactiveLeader;
        private static Point2d _originalStart;
        private static Point2d _originalEnd;
        private static bool _failed;
        private static readonly Point2d EditedStart = new Point2d(-15, 12);
        private static readonly Point2d EditedEnd = new Point2d(85, 40);

        public static void RunSideDatabaseTests(string output)
        {
            using (Database database = new Database(true, true))
            {
                ObjectId leaderId = Create(database);
                Edit(database, leaderId, EditedStart, EditedEnd);
                Sync(database);
                AssertEnds(database, leaderId, EditedStart, EditedEnd);
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Polyline leader = (Polyline)transaction.GetObject(leaderId, OpenMode.ForWrite);
                    leader.AddVertexAt(1, new Point2d(17, -45), 0, 0, 0);
                    transaction.Commit();
                }
                Sync(database);
                AssertEnds(database, leaderId, EditedStart, EditedEnd);
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Polyline leader = (Polyline)transaction.GetObject(leaderId, OpenMode.ForRead);
                    Check(leader.GetPoint2dAt(1).GetDistanceTo(new Point2d(17, -45)) < 1e-6, "Synchronization changed a manually edited interior vertex.");
                }
                ObjectId[] originalDots = DotIds(database, leaderId);

                ObjectId copyId;
                using (IdMapping mapping = new IdMapping())
                {
                    database.DeepCloneObjects(new ObjectIdCollection(new[] { leaderId }), database.CurrentSpaceId, mapping, false);
                    copyId = mapping.Lookup(leaderId).Value;
                }
                Edit(database, copyId, new Point2d(150, 100), new Point2d(250, 150));
                Sync(database);
                AssertEnds(database, copyId, new Point2d(150, 100), new Point2d(250, 150));
                AssertEnds(database, leaderId, EditedStart, EditedEnd);
                ObjectId[] copyDots = DotIds(database, copyId);
                Check(copyDots[0] != originalDots[0] && copyDots[1] != originalDots[1], "COPY leader reused original dots.");

                using (IdMapping mapping = new IdMapping())
                {
                    database.DeepCloneObjects(new ObjectIdCollection(new[] { leaderId, originalDots[0], originalDots[1] }),
                        database.CurrentSpaceId, mapping, false);
                    ObjectId fullCopy = mapping.Lookup(leaderId).Value;
                    Sync(database);
                    ObjectId[] fullCopyDots = DotIds(database, fullCopy);
                    Check(fullCopyDots[0] == mapping.Lookup(originalDots[0]).Value &&
                        fullCopyDots[1] == mapping.Lookup(originalDots[1]).Value, "COPY all entities generated duplicate dots.");
                }

                using (Database destination = new Database(true, true))
                using (IdMapping mapping = new IdMapping())
                {
                    database.WblockCloneObjects(new ObjectIdCollection(new[] { leaderId }), destination.CurrentSpaceId,
                        mapping, DuplicateRecordCloning.Ignore, false);
                    ObjectId destinationId = mapping.Lookup(leaderId).Value;
                    AssertEnds(destination, destinationId, EditedStart, EditedEnd);
                    Handle handle = destinationId.Handle;
                    string path = Path.Combine(output, "linked-leader.dwg");
                    destination.SaveAs(path, DwgVersion.Current);
                    using (Database reopened = new Database(false, true))
                    {
                        reopened.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                        reopened.CloseInput(true);
                        ObjectId reopenedId = reopened.GetObjectId(false, handle, 0);
                        Edit(reopened, reopenedId, new Point2d(-35, 18), new Point2d(110, 70));
                        Sync(reopened);
                        AssertEnds(reopened, reopenedId, new Point2d(-35, 18), new Point2d(110, 70));
                    }
                }
            }
        }

        [CommandMethod("CTLEADERSETUP")]
        public void Setup()
        {
            Guard(() =>
            {
                CalloutWatcher.Initialize();
                Document document = Application.DocumentManager.MdiActiveDocument;
                Database database = document.Database;
                _interactiveLeader = Create(database);
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Polyline leader = (Polyline)transaction.GetObject(_interactiveLeader, OpenMode.ForRead);
                    _originalStart = leader.GetPoint2dAt(0);
                    _originalEnd = leader.GetPoint2dAt(leader.NumberOfVertices - 1);
                }
            });
        }

        [CommandMethod("CTLEADEREDIT")]
        public void EditCommand() => Guard(() => Edit(Application.DocumentManager.MdiActiveDocument.Database, _interactiveLeader, EditedStart, EditedEnd));

        [CommandMethod("CTLEADERCHECKEDIT", CommandFlags.NoUndoMarker)]
        public void CheckEdit() => Guard(() => AssertEnds(Application.DocumentManager.MdiActiveDocument.Database,
            _interactiveLeader, EditedStart, EditedEnd));

        [CommandMethod("CTLEADERCHECKUNDO", CommandFlags.NoUndoMarker)]
        public void CheckUndo() => Guard(() => AssertEnds(Application.DocumentManager.MdiActiveDocument.Database,
            _interactiveLeader, _originalStart, _originalEnd));

        [CommandMethod("CTLEADERFINISH", CommandFlags.NoUndoMarker)]
        public void Finish()
        {
            CalloutWatcher.Terminate();
            if (!_failed) File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CALLOUT_TEST_OUTPUT"), "leader-commands.txt"),
                "PASS: native vertex-edit callbacks, Undo and Redo synchronize both endpoint markers.");
        }

        private static ObjectId Create(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var entities = CalloutGeometryService.CreateSmartLeader(new[] { Point3d.Origin, new Point3d(10, 0, 0),
                    new Point3d(10, 10, 0), new Point3d(0, 10, 0) }, Matrix3d.Displacement(new Vector3d(40, 20, 0)), 2, database);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                foreach (Entity entity in entities)
                {
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                }
                Attach.Invoke(null, new object[] { transaction, entities[0], entities[1], entities[2], 2.0 });
                ObjectId id = entities[0].ObjectId;
                transaction.Commit();
                return id;
            }
        }

        private static void Edit(Database database, ObjectId id, Point2d start, Point2d end)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline leader = (Polyline)transaction.GetObject(id, OpenMode.ForWrite);
                leader.SetPointAt(0, start);
                leader.SetPointAt(leader.NumberOfVertices - 1, end);
                transaction.Commit();
            }
        }

        private static void Sync(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Synchronize.Invoke(null, new object[] { database, transaction });
                transaction.Commit();
            }
        }

        private static ObjectId[] DotIds(Database database, ObjectId leaderId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline leader = (Polyline)transaction.GetObject(leaderId, OpenMode.ForRead);
                DBDictionary dictionary = (DBDictionary)transaction.GetObject(leader.ExtensionDictionary, OpenMode.ForRead);
                Xrecord record = (Xrecord)transaction.GetObject(dictionary.GetAt("TH_CALLOUT_LEADER_V1"), OpenMode.ForRead);
                using (ResultBuffer buffer = record.Data)
                {
                    TypedValue[] values = buffer.AsArray();
                    return new[] { (ObjectId)values[1].Value, (ObjectId)values[2].Value };
                }
            }
        }

        private static void AssertEnds(Database database, ObjectId leaderId, Point2d start, Point2d end)
        {
            ObjectId[] dots = DotIds(database, leaderId);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline leader = (Polyline)transaction.GetObject(leaderId, OpenMode.ForRead);
                Check(leader.GetPoint2dAt(0).GetDistanceTo(start) < 1e-6 &&
                    leader.GetPoint2dAt(leader.NumberOfVertices - 1).GetDistanceTo(end) < 1e-6, "Leader vertex state is wrong.");
                for (int i = 0; i < 2; i++)
                {
                    Polyline dot = (Polyline)transaction.GetObject(dots[i], OpenMode.ForRead);
                    Point3d center = dot.GetPoint3dAt(0) + (dot.GetPoint3dAt(1) - dot.GetPoint3dAt(0)) * 0.5;
                    Point3d endpoint = leader.GetPoint3dAt(i == 0 ? 0 : leader.NumberOfVertices - 1);
                    Check(dot.Closed && dot.NumberOfVertices == 2 && center.DistanceTo(endpoint) < 1e-6, "Endpoint dot did not follow its leader vertex.");
                }
            }
        }

        private static void Guard(Action action)
        {
            try { action(); }
            catch (System.Exception ex)
            {
                _failed = true;
                File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("CALLOUT_TEST_OUTPUT"), "leader-commands.txt"), "FAIL: " + ex + Environment.NewLine);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
