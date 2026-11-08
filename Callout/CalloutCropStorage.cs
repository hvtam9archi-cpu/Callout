using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace Callout.Logic
{
    internal static class CalloutCropStorage
    {
        private const string ReferencesKey = "TH_CALLOUT_CROP_REFS_V1";

        // Persistent hard pointers keep both levels of ACAD_FILTER/SPATIAL in
        // the WblockClone graph, including after a save/reopen or clipboard hop.
        public static bool EnsureReferences(Transaction transaction, DBDictionary extensionDictionary,
            ObjectId filterDictionaryId, ObjectId spatialFilterId)
        {
            Xrecord record = null;
            if (extensionDictionary.Contains(ReferencesKey))
            {
                record = transaction.GetObject(extensionDictionary.GetAt(ReferencesKey), OpenMode.ForRead) as Xrecord;
                if (record == null) throw new InvalidOperationException("Dữ liệu tham chiếu crop không hợp lệ.");
                using (ResultBuffer buffer = record.Data)
                {
                    TypedValue[] values = buffer?.AsArray();
                    if (values != null && values.Length == 3 &&
                        values[0].TypeCode == (int)DxfCode.Int16 && Equals(values[0].Value, (short)1) &&
                        values[1].TypeCode == (int)DxfCode.HardPointerId && Equals(values[1].Value, filterDictionaryId) &&
                        values[2].TypeCode == (int)DxfCode.HardPointerId && Equals(values[2].Value, spatialFilterId) &&
                        record.XlateReferences) return false;
                }
                record.UpgradeOpen();
            }

            using (ResultBuffer data = new ResultBuffer(
                new TypedValue((int)DxfCode.Int16, (short)1),
                new TypedValue((int)DxfCode.HardPointerId, filterDictionaryId),
                new TypedValue((int)DxfCode.HardPointerId, spatialFilterId)))
            {
                if (record != null)
                {
                    record.Data = data;
                    record.XlateReferences = true;
                }
                else
                {
                    Xrecord created = new Xrecord { XlateReferences = true };
                    try
                    {
                        created.Data = data;
                        if (!extensionDictionary.IsWriteEnabled) extensionDictionary.UpgradeOpen();
                        extensionDictionary.SetAt(ReferencesKey, created);
                        transaction.AddNewlyCreatedDBObject(created, true);
                        created = null;
                    }
                    finally { created?.Dispose(); }
                }
            }
            return true;
        }
    }
}
