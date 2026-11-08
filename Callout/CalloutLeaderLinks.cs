using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Callout.Services;

namespace Callout.Logic
{
    internal static class CalloutLeaderLinks
    {
        private const string LeaderKey = "TH_CALLOUT_LEADER_V1";
        private const string OwnerKey = "TH_CALLOUT_LEADER_OWNER_V1";
        private const double PositionTolerance = 1.0e-6;

        public static void Attach(Transaction transaction, Polyline leader, Polyline startDot, Polyline endDot, double diameter)
        {
            Write(transaction, leader, LeaderKey,
                new TypedValue((int)DxfCode.Int16, (short)1),
                new TypedValue((int)DxfCode.HardPointerId, startDot.ObjectId),
                new TypedValue((int)DxfCode.HardPointerId, endDot.ObjectId),
                new TypedValue((int)DxfCode.Real, diameter));
            SetOwner(transaction, startDot, leader.ObjectId);
            SetOwner(transaction, endDot, leader.ObjectId);
        }

        public static void Synchronize(Database database, Transaction transaction)
        {
            // Snapshot IDs before appending replacement markers to the spaces.
            var leaders = new List<ObjectId>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId spaceId in table)
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
                if (!space.IsLayout) continue;
                foreach (ObjectId id in space)
                {
                    if (!IsUsable(id, database)) continue;
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline != null && Read(transaction, polyline, LeaderKey) != null) leaders.Add(id);
                }
            }

            foreach (ObjectId id in leaders)
            {
                Polyline leader = (Polyline)transaction.GetObject(id, OpenMode.ForRead);
                TypedValue[] data = Read(transaction, leader, LeaderKey);
                if (leader.Closed || leader.NumberOfVertices < 2 || data == null || data.Length != 4 ||
                    !VersionMatches(data) || data[1].TypeCode != (int)DxfCode.HardPointerId ||
                    data[2].TypeCode != (int)DxfCode.HardPointerId || data[3].TypeCode != (int)DxfCode.Real ||
                    !(data[1].Value is ObjectId startId) || !(data[2].Value is ObjectId endId) ||
                    !(data[3].Value is double diameter) || diameter <= 0 || double.IsNaN(diameter) || double.IsInfinity(diameter)) continue;
                if (IsLocked(transaction, leader.LayerId)) continue;

                Polyline startDot = UpdateDot(transaction, leader, startId, leader.GetPoint3dAt(0), diameter);
                Polyline endDot = UpdateDot(transaction, leader, endId, leader.GetPoint3dAt(leader.NumberOfVertices - 1), diameter);
                if (startDot.ObjectId != startId || endDot.ObjectId != endId)
                    Attach(transaction, leader, startDot, endDot, diameter);
            }
        }

        private static Polyline UpdateDot(Transaction transaction, Polyline leader, ObjectId markerId, Point3d endpoint, double diameter)
        {
            Polyline dot = IsUsable(markerId, leader.Database) ? transaction.GetObject(markerId, OpenMode.ForRead) as Polyline : null;
            TypedValue[] owner = dot == null ? null : Read(transaction, dot, OwnerKey);
            bool belongsToLeader = owner != null && owner.Length == 2 && VersionMatches(owner) &&
                owner[1].TypeCode == (int)DxfCode.SoftPointerId && Equals(owner[1].Value, leader.ObjectId);
            if (dot == null || !belongsToLeader || dot.OwnerId != leader.OwnerId || dot.NumberOfVertices != 2 || !dot.Closed)
            {
                // COPY of only the leader can retain references to the original
                // markers. Never move those; create a pair owned by the copy.
                Polyline created = (Polyline)CalloutHelpers.CreateLeaderDot(endpoint, diameter, leader.Database);
                try
                {
                    created.SetPropertiesFrom(leader);
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(leader.OwnerId, OpenMode.ForWrite);
                    space.AppendEntity(created);
                    transaction.AddNewlyCreatedDBObject(created, true);
                    dot = created;
                    created = null;
                    SetOwner(transaction, dot, leader.ObjectId);
                }
                finally { created?.Dispose(); }
            }
            else if (!IsLocked(transaction, dot.LayerId))
            {
                Point3d center = dot.GetPoint3dAt(0) + (dot.GetPoint3dAt(1) - dot.GetPoint3dAt(0)) * 0.5;
                if (center.DistanceTo(endpoint) > PositionTolerance)
                {
                    if (!dot.IsWriteEnabled) dot.UpgradeOpen();
                    dot.TransformBy(Matrix3d.Displacement(center.GetVectorTo(endpoint)));
                }
            }
            return dot;
        }

        private static void SetOwner(Transaction transaction, Polyline marker, ObjectId leaderId)
        {
            Write(transaction, marker, OwnerKey,
                new TypedValue((int)DxfCode.Int16, (short)1), new TypedValue((int)DxfCode.SoftPointerId, leaderId));
        }

        private static TypedValue[] Read(Transaction transaction, Entity owner, string key)
        {
            if (owner.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead);
            if (!dictionary.Contains(key)) return null;
            Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (record == null) return null;
            using (ResultBuffer buffer = record.Data) return buffer?.AsArray();
        }

        private static void Write(Transaction transaction, Entity owner, string key, params TypedValue[] values)
        {
            if (owner.ExtensionDictionary.IsNull)
            {
                if (!owner.IsWriteEnabled) owner.UpgradeOpen();
                owner.CreateExtensionDictionary();
            }
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite);
            using (ResultBuffer buffer = new ResultBuffer(values))
            {
                if (dictionary.Contains(key))
                {
                    Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite) as Xrecord;
                    if (record == null) throw new InvalidOperationException("Dữ liệu liên kết đầu nối không hợp lệ.");
                    record.Data = buffer;
                    record.XlateReferences = true;
                }
                else
                {
                    Xrecord record = new Xrecord { XlateReferences = true };
                    try
                    {
                        record.Data = buffer;
                        dictionary.SetAt(key, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                        record = null;
                    }
                    finally { record?.Dispose(); }
                }
            }
        }

        private static bool VersionMatches(TypedValue[] values) => values.Length > 0 &&
            values[0].TypeCode == (int)DxfCode.Int16 && Equals(values[0].Value, (short)1);
        private static bool IsUsable(ObjectId id, Database database) => !id.IsNull && id.IsValid && !id.IsErased && id.Database == database;
        private static bool IsLocked(Transaction transaction, ObjectId layerId) =>
            ((LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForRead)).IsLocked;
    }
}
