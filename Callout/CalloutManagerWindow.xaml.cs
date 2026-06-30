using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using Callout.Services;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Callout.UI
{
    public class CalloutNodeData
    {
        public ObjectId ObjectId { get; set; }
        public bool IsSource { get; set; }
        public int GroupIndex { get; set; }
        public string Title { get; set; }
        public Brush IndicatorColor { get; set; }
        public List<CalloutNodeData> Children { get; set; }

        public CalloutNodeData()
        {
            Children = new List<CalloutNodeData>();
        }
    }

    public partial class CalloutManagerWindow : Window
    {
        private readonly Color[] GroupColors = new Color[] {
            Color.FromRgb(231, 76, 60),  Color.FromRgb(46, 204, 113), Color.FromRgb(52, 152, 219),
            Color.FromRgb(155, 89, 182), Color.FromRgb(241, 196, 15), Color.FromRgb(230, 126, 34),
            Color.FromRgb(26, 188, 156), Color.FromRgb(255, 105, 180), Color.FromRgb(0, 191, 255),
            Color.FromRgb(173, 255, 47), Color.FromRgb(255, 140, 0),  Color.FromRgb(218, 112, 214),
            Color.FromRgb(250, 128, 114),Color.FromRgb(123, 104, 238),Color.FromRgb(240, 230, 140),
            Color.FromRgb(0, 250, 154),  Color.FromRgb(255, 99, 71),  Color.FromRgb(32, 178, 170),
            Color.FromRgb(147, 112, 219),Color.FromRgb(255, 20, 147), Color.FromRgb(100, 149, 237),
            Color.FromRgb(210, 105, 30), Color.FromRgb(154, 205, 50)
        };

        public CalloutManagerWindow()
        {
            InitializeComponent();

            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this)) return;

            LoadTitleIcon("IconRibbon_CalloutManage_32px.ico");
            RefreshData();
            Application.DocumentManager.DocumentActivated += DocumentManager_DocumentActivated;
            this.Closed += (s, e) => Application.DocumentManager.DocumentActivated -= DocumentManager_DocumentActivated;
            
            if (MainTreeView != null) 
            {
                MainTreeView.MouseDoubleClick += (s, e) => GoToSelectedNode();
            }
        }

        private void DocumentManager_DocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            this.Dispatcher.Invoke(() => RefreshData());
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Load icon từ thư mục Resource vào TitleBar.
        /// </summary>
        private void LoadTitleIcon(string fileName)
        {
            try
            {
                string assemblyDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(assemblyDir, "Resource", fileName);
                if (System.IO.File.Exists(path))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.DecodePixelWidth = 22;
                    bi.DecodePixelHeight = 22;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    TitleIcon.Source = bi;
                }
            }
            catch { }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshData();
        }

        private void BtnGoTo_Click(object sender, RoutedEventArgs e)
        {
            GoToSelectedNode();
        }

        private void RefreshData()
        {
            if (MainTreeView == null) return;
            
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var mappings = Callout.Logic.CalloutCoreLogic.GetCalloutMappings(doc.Database);
            if (mappings.Count == 0)
            {
                MainTreeView.ItemsSource = new List<CalloutNodeData> { new CalloutNodeData { Title = "Chưa có dữ liệu trích xuất nào trong bản vẽ này." } };
                return;
            }

            var rootNodes = new List<CalloutNodeData>();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var tbData = new List<Tuple<Extents3d, string>>();
                if (!string.IsNullOrEmpty(Callout.Services.CalloutConfig.TitleBlockName))
                {
                    BlockTableRecord currentSpace = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    foreach (ObjectId id in currentSpace)
                    {
                        if (id.IsErased) continue;
                        BlockReference blk = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (blk != null)
                        {
                            string bName = CalloutHelpers.GetEffectiveBlockName(tr, blk);
                            if (bName.Equals(CalloutConfig.TitleBlockName, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    string sheetNo = CalloutHelpers.GetAttributeValue(tr, blk, CalloutConfig.SheetNumberTag);
                                    if (sheetNo != null) tbData.Add(Tuple.Create(blk.GeometricExtents, sheetNo));
                                }
                                catch { }
                            }
                        }
                    }
                }

                int groupCounter = 0;
                foreach (var kvp in mappings)
                {
                    ObjectId sourceId = kvp.Key;
                    List<Callout.Logic.CalloutBubblePair> pairs = kvp.Value;

                    if (sourceId.IsErased || sourceId.IsNull) continue;
                    BlockReference sourceBlk = tr.GetObject(sourceId, OpenMode.ForRead) as BlockReference;
                    if (sourceBlk == null || sourceBlk.IsErased) continue;

                    string bName = CalloutHelpers.GetEffectiveBlockName(tr, sourceBlk);
                    
                    string sourceSheet = "Trống";
                    try {
                        Point3d pos = sourceBlk.Position;
                        foreach (var tb in tbData)
                        {
                            if (pos.X >= tb.Item1.MinPoint.X && pos.X <= tb.Item1.MaxPoint.X &&
                                pos.Y >= tb.Item1.MinPoint.Y && pos.Y <= tb.Item1.MaxPoint.Y)
                            {
                                sourceSheet = string.IsNullOrEmpty(tb.Item2) ? "Không tên" : tb.Item2;
                                break;
                            }
                        }
                    } catch { }

                    // Tạo gradient chung cho cả block gốc và các chi tiết trích của nó
                    var groupBrush = CreateUniqueGradient(groupCounter);

                    CalloutNodeData sourceNode = new CalloutNodeData {
                        Title = $"Block gốc: {bName} (Bản vẽ: {sourceSheet})",
                        ObjectId = sourceId,
                        IsSource = true,
                        GroupIndex = groupCounter,
                        IndicatorColor = groupBrush
                    };

                    foreach (var pair in pairs)
                    {
                        ObjectId bId = pair.TitleBubbleId;
                        if (bId.IsErased || bId.IsNull) continue;
                        BlockReference titleBubble = tr.GetObject(bId, OpenMode.ForRead) as BlockReference;
                        if (titleBubble != null && !titleBubble.IsErased)
                        {
                            string viewNum = CalloutHelpers.GetAttributeValue(tr, titleBubble, "VIEWNUMBER") ?? "?";
                            string detailSheet = CalloutHelpers.GetAttributeValue(tr, titleBubble, "SHEETNUMBER");
                            if (string.IsNullOrEmpty(detailSheet)) detailSheet = "Trống";

                            CalloutNodeData detailNode = new CalloutNodeData {
                                Title = $"Chi tiết trích: {viewNum} (Bản vẽ: {detailSheet})",
                                ObjectId = bId,
                                IsSource = false,
                                GroupIndex = groupCounter,
                                IndicatorColor = groupBrush
                            };
                            sourceNode.Children.Add(detailNode);
                        }
                    }

                    if (sourceNode.Children.Count > 0)
                    {
                        rootNodes.Add(sourceNode);
                        groupCounter++;
                    }
                }

                tr.Commit();
            }
            
            MainTreeView.ItemsSource = rootNodes;
            
            // Expand all by default — kế thừa Style gốc từ Resources, chỉ thêm IsExpanded
            Style baseStyle = MainTreeView.FindResource(typeof(TreeViewItem)) as Style;
            Style expandedStyle = new Style(typeof(TreeViewItem), baseStyle);
            expandedStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, true));
            MainTreeView.ItemContainerStyle = expandedStyle;
        }

        /// <summary>
        /// Tạo một LinearGradientBrush duy nhất cho mỗi index bằng thuật toán Golden Angle.
        /// Mỗi index sẽ cho ra một cặp Hue khác nhau, đảm bảo không trùng lặp dù có bao nhiêu node.
        /// </summary>
        private LinearGradientBrush CreateUniqueGradient(int index)
        {
            // Golden Angle (deg) ≈ 137.508° → phân bố Hue đều trên vòng tròn màu
            double goldenAngle = 137.508;
            
            double hue1 = (index * goldenAngle) % 360.0;
            double hue2 = ((index * goldenAngle) + 60.0) % 360.0; // Dịch 60° cho gradient stop thứ 2
            
            var color1 = HslToColor(hue1, 0.7, 0.55);
            var color2 = HslToColor(hue2, 0.65, 0.45);
            
            var brush = new LinearGradientBrush()
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop(color1, 0.0));
            brush.GradientStops.Add(new GradientStop(color2, 1.0));
            return brush;
        }

        /// <summary>
        /// Chuyển đổi HSL (Hue 0-360, Saturation 0-1, Lightness 0-1) sang WPF Color.
        /// </summary>
        private System.Windows.Media.Color HslToColor(double hue, double saturation, double lightness)
        {
            double c = (1.0 - Math.Abs(2.0 * lightness - 1.0)) * saturation;
            double x = c * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
            double m = lightness - c / 2.0;

            double r = 0, g = 0, b = 0;

            if (hue < 60)       { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else                { r = c; g = 0; b = x; }

            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255)
            );
        }



        private void GoToSelectedNode()
        {
            var data = MainTreeView.SelectedItem as CalloutNodeData;
            if (data == null || data.ObjectId == null) return;
            
            ObjectId targetId = data.ObjectId;

            if (targetId.IsNull || targetId.IsErased)
            {
                MessageBox.Show("Đối tượng không còn tồn tại trên bản vẽ.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var doc = Application.DocumentManager.MdiActiveDocument;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockReference ent = tr.GetObject(targetId, OpenMode.ForRead) as BlockReference;
                if (ent != null)
                {
                    try {
                        ZoomToExtents(doc.Editor, ent, data.IsSource);
                    } catch { }
                }
                tr.Commit();
            }
        }

        private void ZoomToExtents(Editor ed, BlockReference ent, bool isSource)
        {
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                Matrix3d WCS2DCS = Matrix3d.PlaneToWorld(view.ViewDirection);
                WCS2DCS = Matrix3d.Displacement(view.Target - Point3d.Origin) * WCS2DCS;
                WCS2DCS = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * WCS2DCS;
                WCS2DCS = WCS2DCS.Inverse();

                double width, height;
                Point3d targetCenter;

                if (isSource)
                {
                    Extents3d ext = ent.GeometricExtents;
                    ext.TransformBy(WCS2DCS);
                    width = (ext.MaxPoint.X - ext.MinPoint.X) * 1.5;
                    height = (ext.MaxPoint.Y - ext.MinPoint.Y) * 1.5;
                    targetCenter = new Point3d((ext.MaxPoint.X + ext.MinPoint.X) / 2.0, (ext.MaxPoint.Y + ext.MinPoint.Y) / 2.0, 0);
                }
                else
                {
                    double dimScale = ent.ScaleFactors.X;
                    width = 150.0 * dimScale;
                    height = 100.0 * dimScale;
                    
                    Point3d bubbleCenter = ent.Position;
                    bubbleCenter = bubbleCenter.TransformBy(WCS2DCS);
                    
                    targetCenter = new Point3d(bubbleCenter.X - (width * 0.2), bubbleCenter.Y + (height * 0.3), 0);
                }

                if (width < 100) width = 100;
                if (height < 100) height = 100;

                view.Width = width;
                view.Height = height;
                view.CenterPoint = new Autodesk.AutoCAD.Geometry.Point2d(targetCenter.X, targetCenter.Y);

                ed.SetCurrentView(view);
            }
        }
    }
}
