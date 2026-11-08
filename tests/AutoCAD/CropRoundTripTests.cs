using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Callout.Logic;

namespace Callout.NativeTests
{
    public class CropRoundTripTests
    {
        private static readonly MethodInfo ApplyClip = typeof(CalloutCoreLogic).GetMethod("ApplyXClip", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo RepairClip = typeof(CalloutCoreLogic).GetMethod("EnsureSpatialFilterCloneable", BindingFlags.NonPublic | BindingFlags.Static);

        [CommandMethod("CTNATIVEREGRESSION", CommandFlags.Modal)]
        public void Run()
        {
            string output = Environment.GetEnvironmentVariable("CALLOUT_TEST_OUTPUT");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            string result = Path.Combine(output, "result.txt");
            try
            {
                using (var sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(typeof(CalloutCoreLogic).Assembly.Location))).Replace("-", "");
                    Check(hash == Environment.GetEnvironmentVariable("CALLOUT_EXPECTED_HASH"), "The host loaded a different Callout.dll.");
                }
                RunCase(output, "translated", Matrix3d.Displacement(new Vector3d(1400, -750, 0)), false);
                RunCase(output, "rotated-scaled", Matrix3d.Displacement(new Vector3d(-850, 2000, 0)) *
                    Matrix3d.Rotation(0.45, Vector3d.ZAxis, Point3d.Origin) * Matrix3d.Scaling(2.5, Point3d.Origin), false);
                RunCase(output, "mirrored", Matrix3d.Displacement(new Vector3d(300, 400, 0)) *
                    Matrix3d.Mirroring(new Plane(Point3d.Origin, Vector3d.XAxis)), false);
                RunCase(output, "legacy-repaired", Matrix3d.Identity, true);
                LeaderLinkTests.RunSideDatabaseTests(output);
                File.WriteAllText(result, "PASS: four crop roundtrips; linked-leader vertex editing, independent COPY, WBLOCK and save/reopen.");
            }
            catch (System.Exception ex)
            {
                File.WriteAllText(result, "FAIL: " + ex);
            }
        }

        private static void RunCase(string output, string name, Matrix3d transform, bool testRepair)
        {
            string sourcePath = Path.Combine(output, name + "-source.dwg");
            string clipboardPath = Path.Combine(output, name + "-clipboard.dwg");
            string destinationPath = Path.Combine(output, name + "-destination.dwg");
            Handle sourceHandle;
            using (Database source = new Database(true, true))
            {
                ObjectId sourceId = CreateClippedBlock(source, transform);
                AssertCrop(source, sourceId);
                sourceHandle = sourceId.Handle;
                source.SaveAs(sourcePath, DwgVersion.Current);
            }

            using (Database reopened = Open(sourcePath))
            {
                ObjectId sourceId = reopened.GetObjectId(false, sourceHandle, 0);
                AssertCrop(reopened, sourceId);
                if (testRepair)
                {
                    using (Transaction transaction = reopened.TransactionManager.StartTransaction())
                    {
                        BlockReference block = (BlockReference)transaction.GetObject(sourceId, OpenMode.ForRead);
                        DBDictionary extension = (DBDictionary)transaction.GetObject(block.ExtensionDictionary, OpenMode.ForWrite);
                        ObjectId anchorId = extension.GetAt("TH_CALLOUT_CROP_REFS_V1");
                        extension.Remove("TH_CALLOUT_CROP_REFS_V1");
                        transaction.GetObject(anchorId, OpenMode.ForWrite).Erase();
                        object[] arguments = { transaction, block, false };
                        Check((bool)RepairClip.Invoke(null, arguments) && (bool)arguments[2], "Legacy crop was not repaired.");
                        transaction.Commit();
                    }
                }
                using (Database clipboard = new Database(true, true))
                {
                    ObjectId clipboardId = Copy(reopened, sourceId, clipboard);
                    AssertCrop(clipboard, clipboardId);
                    Handle clipboardHandle = clipboardId.Handle;
                    clipboard.SaveAs(clipboardPath, DwgVersion.Current);
                    using (Database clipboardReopened = Open(clipboardPath))
                    {
                        clipboardId = clipboardReopened.GetObjectId(false, clipboardHandle, 0);
                        AssertCrop(clipboardReopened, clipboardId);
                        using (Database destination = new Database(true, true))
                        {
                            ObjectId destinationId = Copy(clipboardReopened, clipboardId, destination);
                            AssertCrop(destination, destinationId);
                            Handle destinationHandle = destinationId.Handle;
                            destination.SaveAs(destinationPath, DwgVersion.Current);
                            using (Database destinationReopened = Open(destinationPath))
                                AssertCrop(destinationReopened, destinationReopened.GetObjectId(false, destinationHandle, 0));
                        }
                    }
                }
            }
        }

        private static ObjectId CreateClippedBlock(Database database, Matrix3d transform)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
                var definition = new BlockTableRecord { Name = "CALLOUT_CROP_REGRESSION" };
                table.Add(definition);
                transaction.AddNewlyCreatedDBObject(definition, true);
                var shape = new Circle(new Point3d(50, 50, 0), Vector3d.ZAxis, 45);
                definition.AppendEntity(shape);
                transaction.AddNewlyCreatedDBObject(shape, true);
                var block = new BlockReference(Point3d.Origin, definition.ObjectId);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(block);
                transaction.AddNewlyCreatedDBObject(block, true);
                block.TransformBy(transform);
                using (var boundary = new Polyline(4))
                {
                    boundary.AddVertexAt(0, new Point2d(20, 20), 0, 0, 0);
                    boundary.AddVertexAt(1, new Point2d(60, 20), 0, 0, 0);
                    boundary.AddVertexAt(2, new Point2d(60, 60), 0, 0, 0);
                    boundary.AddVertexAt(3, new Point2d(20, 60), 0, 0, 0);
                    boundary.Closed = true;
                    boundary.TransformBy(transform);
                    ApplyClip.Invoke(null, new object[] { transaction, block, boundary });
                    // Replacing an existing crop must also update its hard pointers.
                    ApplyClip.Invoke(null, new object[] { transaction, block, boundary });
                }
                transaction.Commit();
                return block.ObjectId;
            }
        }

        private static ObjectId Copy(Database source, ObjectId sourceId, Database destination)
        {
            using (IdMapping mapping = new IdMapping())
            {
                source.WblockCloneObjects(new ObjectIdCollection(new[] { sourceId }), destination.CurrentSpaceId,
                    mapping, DuplicateRecordCloning.Ignore, false);
                return mapping.Lookup(sourceId).Value;
            }
        }

        private static Database Open(string path)
        {
            Database database = new Database(false, true);
            try
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                database.CloseInput(true);
                return database;
            }
            catch (System.Exception)
            {
                database.Dispose();
                throw;
            }
        }

        private static void AssertCrop(Database database, ObjectId blockId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference block = (BlockReference)transaction.GetObject(blockId, OpenMode.ForRead);
                Check(!block.ExtensionDictionary.IsNull, "Missing extension dictionary.");
                DBDictionary extension = (DBDictionary)transaction.GetObject(block.ExtensionDictionary, OpenMode.ForRead);
                Check(extension.Contains("ACAD_FILTER"), "ACAD_FILTER was lost.");
                ObjectId dictionaryId = extension.GetAt("ACAD_FILTER");
                DBDictionary filters = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForRead);
                Check(filters.Contains("SPATIAL"), "SPATIAL was lost.");
                ObjectId filterId = filters.GetAt("SPATIAL");
                SpatialFilter filter = (SpatialFilter)transaction.GetObject(filterId, OpenMode.ForRead);
                Check(filter.Definition.Enabled && filter.Definition.GetPoints().Count == 4, "Crop was disabled or lost its polygon.");
                foreach (Point2d point in filter.Definition.GetPoints())
                    Check((Near(point.X, 20) || Near(point.X, 60)) && (Near(point.Y, 20) || Near(point.Y, 60)), "Crop coordinates changed.");
                Check(extension.Contains("TH_CALLOUT_CROP_REFS_V1"), "Persistent crop references were lost.");
                Xrecord record = (Xrecord)transaction.GetObject(extension.GetAt("TH_CALLOUT_CROP_REFS_V1"), OpenMode.ForRead);
                using (ResultBuffer buffer = record.Data)
                {
                    TypedValue[] values = buffer.AsArray();
                    Check(record.XlateReferences && values.Length == 3 &&
                        values[1].TypeCode == (int)DxfCode.HardPointerId && Equals(values[1].Value, dictionaryId) &&
                        values[2].TypeCode == (int)DxfCode.HardPointerId && Equals(values[2].Value, filterId),
                        "Hard pointers did not translate to the destination database.");
                }
            }
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
