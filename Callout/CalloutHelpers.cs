using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Callout.Services
{
    /// <summary>
    /// Các hàm tiện ích dùng chung cho toàn bộ plugin Callout.
    /// DRY: Tách logic lặp lại thành 1 nơi duy nhất.
    /// </summary>
    public static class CalloutHelpers
    {
        /// <summary>
        /// Đọc giá trị Attribute Tag từ BlockReference.
        /// </summary>
        public static string GetAttributeValue(Transaction transaction, BlockReference blockRef, string tag)
        {
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = transaction.GetObject(attId, OpenMode.ForRead, false, true) as AttributeReference;
                if (attRef != null && attRef.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return attRef.TextString;
                }
            }
            return null;
        }

        /// <summary>
        /// Ghi giá trị Attribute Tag vào BlockReference.
        /// </summary>
        public static void SetAttributeValue(Transaction transaction, BlockReference blockRef, string tag, string value)
        {
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = transaction.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                if (attRef != null && attRef.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    attRef.TextString = value;
                    return;
                }
            }
        }

        /// <summary>
        /// Gán Attribute từ Dictionary key-value vào BlockReference (tạo mới nếu chưa có).
        /// </summary>
        public static void ApplyDictionaryAttributes(Transaction transaction, BlockReference blockRef, Dictionary<string, string> attributeValues)
        {
            BlockTableRecord blockDef = (BlockTableRecord)transaction.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
            if (!blockDef.HasAttributeDefinitions) return;

            HashSet<string> existingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                AttributeReference existingAtt = transaction.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                if (existingAtt != null)
                {
                    existingTags.Add(existingAtt.Tag);
                    if (attributeValues.TryGetValue(existingAtt.Tag, out string newVal))
                    {
                        existingAtt.TextString = newVal;
                    }
                }
            }

            foreach (ObjectId id in blockDef)
            {
                var dbObj = transaction.GetObject(id, OpenMode.ForRead);
                if (dbObj is AttributeDefinition attDef && !attDef.Constant)
                {
                    if (existingTags.Contains(attDef.Tag)) continue;

                    using (AttributeReference attRef = new AttributeReference())
                    {
                        attRef.SetAttributeFromBlock(attDef, blockRef.BlockTransform);
                        attRef.Position = attDef.Position.TransformBy(blockRef.BlockTransform);

                        if (attributeValues.TryGetValue(attDef.Tag, out string tagValue))
                        {
                            attRef.TextString = tagValue;
                        }

                        blockRef.AttributeCollection.AppendAttribute(attRef);
                        transaction.AddNewlyCreatedDBObject(attRef, true);
                    }
                }
            }
        }

        /// <summary>
        /// Đảm bảo Layer tồn tại với thuộc tính mong muốn. Tạo mới nếu chưa có.
        /// </summary>
        public static ObjectId EnsureLayer(Database database, Transaction transaction, string layerName, Color color, string lineTypeName, LineWeight lineWeight)
        {
            using (LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForWrite))
            {
                ObjectId lineTypeId = SymbolUtilityServices.GetLinetypeContinuousId(database);

                if (!string.IsNullOrEmpty(lineTypeName))
                {
                    using (LinetypeTable linetypeTable = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead))
                    {
                        if (!linetypeTable.Has(lineTypeName))
                        {
                            string filename = (short)Application.GetSystemVariable("MEASUREMENT") == 0 ? "acad.lin" : "acadiso.lin";
                            try
                            {
                                database.LoadLineTypeFile(lineTypeName, filename);
                            }
                            catch (Exception ex)
                            {
                                Application.DocumentManager.MdiActiveDocument?.Editor
                                    .WriteMessage($"\n[Cảnh báo] Load Linetype '{lineTypeName}' lỗi: {ex.Message}");
                            }
                        }

                        if (linetypeTable.Has(lineTypeName))
                        {
                            lineTypeId = linetypeTable[lineTypeName];
                        }
                    }
                }

                if (layerTable.Has(layerName))
                {
                    using (LayerTableRecord layerRecord = (LayerTableRecord)transaction.GetObject(layerTable[layerName], OpenMode.ForWrite))
                    {
                        layerRecord.Color = color;
                        layerRecord.LinetypeObjectId = lineTypeId;
                        layerRecord.LineWeight = lineWeight;
                        return layerRecord.ObjectId;
                    }
                }
                else
                {
                    using (LayerTableRecord layerRecord = new LayerTableRecord { Name = layerName })
                    {
                        layerTable.Add(layerRecord);
                        transaction.AddNewlyCreatedDBObject(layerRecord, true);

                        layerRecord.Color = color;
                        layerRecord.LinetypeObjectId = lineTypeId;
                        layerRecord.LineWeight = lineWeight;

                        return layerRecord.ObjectId;
                    }
                }
            }
        }

        /// <summary>
        /// Đảm bảo TextStyle tồn tại. Tạo mới nếu chưa có.
        /// </summary>
        public static ObjectId EnsureTextStyle(Database database, Transaction transaction, string styleName, string fontName)
        {
            using (TextStyleTable textStyleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForWrite))
            {
                if (textStyleTable.Has(styleName)) return textStyleTable[styleName];

                using (TextStyleTableRecord textStyleRecord = new TextStyleTableRecord())
                {
                    textStyleRecord.Name = styleName;
                    textStyleRecord.FileName = fontName;

                    textStyleTable.Add(textStyleRecord);
                    transaction.AddNewlyCreatedDBObject(textStyleRecord, true);
                    return textStyleRecord.ObjectId;
                }
            }
        }

        /// <summary>
        /// Lấy ObjectId của Arrow Block, tự tạo nếu chưa tồn tại.
        /// </summary>
        public static ObjectId GetArrowBlockId(Database database, Transaction transaction, string blockName)
        {
            using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead))
            {
                if (blockTable.Has(blockName)) return blockTable[blockName];

                try
                {
                    Application.SetSystemVariable("DIMBLK", blockName);
                    if (blockTable.Has(blockName)) return blockTable[blockName];
                }
                catch (Exception ex)
                {
                    Application.DocumentManager.MdiActiveDocument?.Editor
                        .WriteMessage($"\n[Cảnh báo] Lấy DB Arrow Block {blockName} lỗi: {ex.Message}");
                }
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Tạo filled dot (donut) tại vị trí center.
        /// </summary>
        public static Entity CreateLeaderDot(Point3d center, double diameter)
        {
            Polyline dot = new Polyline();
            dot.SetDatabaseDefaults();
            double radius = diameter / 2.0;

            dot.AddVertexAt(0, new Point2d(center.X - radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.AddVertexAt(1, new Point2d(center.X + radius / 2.0, center.Y), 1.0, diameter, diameter);
            dot.Closed = true;

            return dot;
        }

        /// <summary>
        /// Lấy tên Block (hỗ trợ Dynamic Block).
        /// </summary>
        public static string GetEffectiveBlockName(Transaction transaction, BlockReference blockRef)
        {
            if (blockRef.IsDynamicBlock)
            {
                BlockTableRecord dynBtr = (BlockTableRecord)transaction.GetObject(blockRef.DynamicBlockTableRecord, OpenMode.ForRead);
                return dynBtr.Name;
            }
            return blockRef.Name;
        }

        /// <summary>
        /// Gán Fixed Extension Line cho Dimension qua Reflection.
        /// </summary>
        public static void SetFixedExtensionLine(Dimension dimension, double length)
        {
            try
            {
                var type = dimension.GetType();
                var propertyOn = type.GetProperty("Dimfxlon");
                var propertyLength = type.GetProperty("Dimfxl");

                if (propertyOn != null && propertyLength != null)
                {
                    propertyOn.SetValue(dimension, true, null);
                    propertyLength.SetValue(dimension, length, null);
                }
            }
            catch (Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[Cảnh báo] SetFixedExtensionLine lỗi: {ex.Message}");
            }
        }

        /// <summary>
        /// Đảm bảo DimStyle cho Callout tồn tại. Tạo mới nếu chưa có.
        /// </summary>
        public static ObjectId EnsureCalloutDimStyle(Database database, Transaction transaction, double dimensionScale, string styleName)
        {
            using (DimStyleTable dimensionTable = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForWrite))
            {
                if (dimensionTable.Has(styleName))
                {
                    using (DimStyleTableRecord existingRecord = (DimStyleTableRecord)transaction.GetObject(dimensionTable[styleName], OpenMode.ForWrite))
                    {
                        return existingRecord.ObjectId;
                    }
                }

                using (DimStyleTableRecord dimensionStyle = new DimStyleTableRecord { Name = styleName })
                {
                    dimensionTable.Add(dimensionStyle);
                    transaction.AddNewlyCreatedDBObject(dimensionStyle, true);

                    dimensionStyle.Dimtxsty = EnsureTextStyle(database, transaction, "Standard", "arial.ttf");
                    dimensionStyle.Dimtxt = 2.0;
                    dimensionStyle.Dimtih = false;
                    dimensionStyle.Dimtoh = true;
                    dimensionStyle.Dimtad = 1;
                    dimensionStyle.Dimgap = 0.6;
                    dimensionStyle.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, 2);
                    dimensionStyle.Dimtfill = 1;

                    dimensionStyle.Dimscale = dimensionScale;
                    dimensionStyle.Dimasz = 1.0;
                    dimensionStyle.Dimexo = 0.0;
                    dimensionStyle.Dimdli = 6.0;
                    dimensionStyle.Dimexe = 1.0;
                    dimensionStyle.Dimdle = 1.0;
                    dimensionStyle.Dimcen = 0.09;

                    try
                    {
                        var propOn = dimensionStyle.GetType().GetProperty("Dimfxlon");
                        var propLen = dimensionStyle.GetType().GetProperty("Dimfxl");
                        propOn?.SetValue(dimensionStyle, true, null);
                        propLen?.SetValue(dimensionStyle, 6.0, null);
                    }
                    catch (Exception ex)
                    {
                        Application.DocumentManager.MdiActiveDocument?.Editor
                            .WriteMessage($"\n[Cảnh báo] Dimfxlon trên DimStyle lỗi: {ex.Message}");
                    }

                    ObjectId architecturalTickId = GetArrowBlockId(database, transaction, "_ARCHTICK");
                    if (!architecturalTickId.IsNull) dimensionStyle.Dimblk = architecturalTickId;

                    dimensionStyle.Dimlunit = 2;
                    dimensionStyle.Dimdec = 1;
                    dimensionStyle.Dimtdec = 1;
                    dimensionStyle.Dimdsep = '.';
                    dimensionStyle.Dimzin = 8;

                    dimensionStyle.Dimclrd = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    dimensionStyle.Dimclre = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    dimensionStyle.Dimlwd = LineWeight.ByBlock;
                    dimensionStyle.Dimlwe = LineWeight.ByBlock;

                    return dimensionStyle.ObjectId;
                }
            }
        }

        /// <summary>
        /// Đảm bảo Block "_DetailCallout - Metric" tồn tại. Tạo mới nếu chưa có.
        /// </summary>
        public static ObjectId EnsureDetailCalloutBlock(Database database, Transaction transaction)
        {
            string blockName = "_DetailCallout - Metric";
            using (BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite))
            {
                if (blockTable.Has(blockName)) return blockTable[blockName];

                using (BlockTableRecord btr = new BlockTableRecord())
                {
                    btr.Name = blockName;
                    btr.Origin = Point3d.Origin;

                    Circle c1 = new Circle(Point3d.Origin, Vector3d.ZAxis, 5.5);
                    c1.SetDatabaseDefaults();
                    btr.AppendEntity(c1);

                    Circle c2 = new Circle(Point3d.Origin, Vector3d.ZAxis, 6.0);
                    c2.SetDatabaseDefaults();
                    btr.AppendEntity(c2);

                    Line l1 = new Line(new Point3d(-6.0, 0, 0), new Point3d(6.0, 0, 0));
                    l1.SetDatabaseDefaults();
                    btr.AppendEntity(l1);

                    ObjectId abcVerdanaStyleId = EnsureTextStyle(database, transaction, "ABC_Verdana", "verdana.ttf");

                    AttributeDefinition ad1 = new AttributeDefinition();
                    ad1.SetDatabaseDefaults();
                    ad1.HorizontalMode = TextHorizontalMode.TextCenter;
                    ad1.VerticalMode = TextVerticalMode.TextBase;
                    ad1.AlignmentPoint = new Point3d(0, 1.5, 0);
                    ad1.Position = new Point3d(0, 1.5, 0);
                    ad1.Height = 2.5;
                    ad1.Tag = "VIEWNUMBER";
                    ad1.TextString = "VIEWNUMBER";
                    ad1.Prompt = "Enter view number";
                    ad1.TextStyleId = abcVerdanaStyleId;
                    btr.AppendEntity(ad1);

                    AttributeDefinition ad2 = new AttributeDefinition();
                    ad2.SetDatabaseDefaults();
                    ad2.HorizontalMode = TextHorizontalMode.TextCenter;
                    ad2.VerticalMode = TextVerticalMode.TextVerticalMid;
                    ad2.AlignmentPoint = new Point3d(0, -2.0, 0);
                    ad2.Position = new Point3d(0, -2.0, 0);
                    ad2.Height = 2.0;
                    ad2.Tag = "SHEETNUMBER";
                    ad2.TextString = "SHEETNUMBER";
                    ad2.Prompt = "Enter sheet number";
                    ad2.TextStyleId = abcVerdanaStyleId;
                    btr.AppendEntity(ad2);

                    blockTable.Add(btr);
                    transaction.AddNewlyCreatedDBObject(btr, true);
                    return btr.ObjectId;
                }
            }
        }
    }
}
