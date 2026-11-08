using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace Callout.Logic
{
    internal static class CalloutMappingRepository
    {
        private const string LinkKey = "TH_CALLOUT_LINK_V1";

        public static void Save(Transaction transaction, BlockReference titleBubble, ObjectId sourceId, ObjectId sourceBubbleId)
        {
            if (titleBubble.ExtensionDictionary.IsNull) titleBubble.CreateExtensionDictionary();
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(titleBubble.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord record = new Xrecord { XlateReferences = true };
            bool added = false;
            try
            {
                using (ResultBuffer data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Int16, (short)1),
                    new TypedValue((int)DxfCode.SoftPointerId, sourceId),
                    new TypedValue((int)DxfCode.SoftPointerId, sourceBubbleId)))
                {
                    record.Data = data;
                }
                dictionary.SetAt(LinkKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
                added = true;
            }
            finally
            {
                if (!added) record.Dispose();
            }
        }

        public static Dictionary<ObjectId, List<CalloutBubblePair>> Read(Database database, Transaction transaction)
        {
            var mappings = new Dictionary<ObjectId, List<CalloutBubblePair>>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId spaceId in table)
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
                if (!space.IsLayout) continue;
                foreach (ObjectId id in space)
                {
                    if (!IsUsable(id, database)) continue;
                    BlockReference titleBubble = transaction.GetObject(id, OpenMode.ForRead, false, true) as BlockReference;
                    if (titleBubble == null || titleBubble.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = (DBDictionary)transaction.GetObject(titleBubble.ExtensionDictionary, OpenMode.ForRead);
                    if (!dictionary.Contains(LinkKey)) continue;
                    Xrecord record = transaction.GetObject(dictionary.GetAt(LinkKey), OpenMode.ForRead) as Xrecord;
                    if (record == null) continue;

                    using (ResultBuffer data = record.Data)
                    {
                        TypedValue[] values = data?.AsArray();
                        if (values == null || values.Length != 3 ||
                            values[0].TypeCode != (int)DxfCode.Int16 || !Equals(values[0].Value, (short)1) ||
                            values[1].TypeCode != (int)DxfCode.SoftPointerId ||
                            values[2].TypeCode != (int)DxfCode.SoftPointerId ||
                            !(values[1].Value is ObjectId sourceId) || !(values[2].Value is ObjectId sourceBubbleId) ||
                            !IsUsable(sourceId, database)) continue;

                        if (!(transaction.GetObject(sourceId, OpenMode.ForRead, false, true) is BlockReference)) continue;
                        if (!IsUsable(sourceBubbleId, database) ||
                            !(transaction.GetObject(sourceBubbleId, OpenMode.ForRead, false, true) is BlockReference))
                            sourceBubbleId = ObjectId.Null;

                        if (!mappings.TryGetValue(sourceId, out List<CalloutBubblePair> pairs))
                        {
                            pairs = new List<CalloutBubblePair>();
                            mappings.Add(sourceId, pairs);
                        }
                        pairs.Add(new CalloutBubblePair { SourceBubbleId = sourceBubbleId, TitleBubbleId = id });
                    }
                }
            }
            return mappings;
        }

        private static bool IsUsable(ObjectId id, Database database)
        {
            return !id.IsNull && id.IsValid && !id.IsErased && id.Database == database;
        }
    }
}
