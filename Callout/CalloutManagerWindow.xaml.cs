using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
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

    public class CalloutManagerWindow : Window
    {
        private TreeView treeView;
        private Button btnRefresh;
        private Button btnGoTo;

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
            InitializeUI();
            RefreshData();
            Application.DocumentManager.DocumentActivated += DocumentManager_DocumentActivated;
            this.Closed += (s, e) => Application.DocumentManager.DocumentActivated -= DocumentManager_DocumentActivated;
        }

        private void DocumentManager_DocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            this.Dispatcher.Invoke(() => RefreshData());
        }

        private void InitializeUI()
        {
            string xaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Title=""Quản lý Chi tiết Trích (CTS)"" Width=""600"" Height=""500""
        WindowStartupLocation=""CenterScreen"" ResizeMode=""CanResizeWithGrip""
        WindowStyle=""None"" AllowsTransparency=""True""
        Background=""Transparent"" Foreground=""#E8EAED"" FontFamily=""Segoe UI"">
    <Window.Resources>
        <!-- ═══════════ GRADIENT BRUSHES ═══════════ -->
        <LinearGradientBrush x:Key=""NodeBg"" StartPoint=""0,0"" EndPoint=""0,1"">
            <GradientStop Color=""#2B2D32"" Offset=""0""/>
            <GradientStop Color=""#232529"" Offset=""1""/>
        </LinearGradientBrush>
        <LinearGradientBrush x:Key=""NodeBgHover"" StartPoint=""0,0"" EndPoint=""0,1"">
            <GradientStop Color=""#353840"" Offset=""0""/>
            <GradientStop Color=""#2B2D34"" Offset=""1""/>
        </LinearGradientBrush>
        <LinearGradientBrush x:Key=""NodeBgSelected"" StartPoint=""0,0"" EndPoint=""1,0"">
            <GradientStop Color=""#1A4A6E"" Offset=""0""/>
            <GradientStop Color=""#0D3A5C"" Offset=""1""/>
        </LinearGradientBrush>

        <!-- ═══════════ TREEVIEWITEM TEMPLATE ═══════════ -->
        <Style TargetType=""TreeViewItem"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""Padding"" Value=""0""/>
            <Setter Property=""Margin"" Value=""0,2,0,2""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""TreeViewItem"">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height=""Auto""/>
                                <RowDefinition/>
                            </Grid.RowDefinitions>

                            <!-- ROW: Caret + Card -->
                            <Grid Grid.Row=""0"">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width=""24""/>
                                    <ColumnDefinition Width=""*""/>
                                </Grid.ColumnDefinitions>

                                <!-- Expand/Collapse Caret -->
                                <ToggleButton x:Name=""Expander""
                                    Style=""{DynamicResource CaretToggleStyle}""
                                    IsChecked=""{Binding Path=IsExpanded, RelativeSource={RelativeSource TemplatedParent}}""
                                    ClickMode=""Press"" VerticalAlignment=""Center""/>

                                <!-- Node Card -->
                                <Border Name=""Bd"" Grid.Column=""1""
                                    Background=""{StaticResource NodeBg}""
                                    BorderBrush=""#333640"" BorderThickness=""1""
                                    CornerRadius=""8"" MinHeight=""42"" Margin=""0,1,0,1""
                                    SnapsToDevicePixels=""True"">
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width=""14""/>
                                            <ColumnDefinition Width=""*""/>
                                        </Grid.ColumnDefinitions>

                                        <!-- Gradient Indicator Bar (5px) -->
                                        <Border Grid.Column=""0"" Background=""{Binding IndicatorColor}""
                                            CornerRadius=""4,0,0,4"" Margin=""0"" Width=""5""/>

                                        <!-- Text Content -->
                                        <ContentPresenter x:Name=""PART_Header""
                                            Grid.Column=""1"" ContentSource=""Header""
                                            HorizontalAlignment=""Left"" VerticalAlignment=""Center""
                                            Margin=""14,8,12,8""/>
                                    </Grid>
                                </Border>
                            </Grid>

                            <!-- CHILDREN CONTAINER -->
                            <Grid Grid.Row=""1"" Margin=""12,0,0,0"">
                                <!-- Vertical indent guide line -->
                                <Border BorderThickness=""1,0,0,0""
                                    BorderBrush=""#2A2D33""
                                    Margin=""12,0,0,0""
                                    HorizontalAlignment=""Left""/>
                                <ItemsPresenter x:Name=""ItemsHost"" Margin=""24,0,0,0""/>
                            </Grid>
                        </Grid>

                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsExpanded"" Value=""false"">
                                <Setter TargetName=""ItemsHost"" Property=""Visibility"" Value=""Collapsed""/>
                            </Trigger>
                            <Trigger Property=""HasItems"" Value=""false"">
                                <Setter TargetName=""Expander"" Property=""Visibility"" Value=""Hidden""/>
                            </Trigger>
                            <Trigger Property=""IsSelected"" Value=""true"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{StaticResource NodeBgSelected}""/>
                                <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""#1E6DA0""/>
                            </Trigger>
                            <MultiTrigger>
                                <MultiTrigger.Conditions>
                                    <Condition Property=""IsSelected"" Value=""false""/>
                                    <Condition Property=""IsMouseOver"" Value=""true"" SourceName=""Bd""/>
                                </MultiTrigger.Conditions>
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{StaticResource NodeBgHover}""/>
                                <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""#454850""/>
                            </MultiTrigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ═══════════ CARET TOGGLE ═══════════ -->
        <Style x:Key=""CaretToggleStyle"" TargetType=""ToggleButton"">
            <Setter Property=""Focusable"" Value=""False""/>
            <Setter Property=""Width"" Value=""22""/>
            <Setter Property=""Height"" Value=""22""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ToggleButton"">
                        <Border Background=""Transparent"" CornerRadius=""4"" Width=""22"" Height=""22"">
                            <Path x:Name=""Arrow""
                                Fill=""#6B7280""
                                Data=""M 5 3 L 5 11 L 11 7 Z""
                                VerticalAlignment=""Center""
                                HorizontalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsChecked"" Value=""True"">
                                <Setter TargetName=""Arrow"" Property=""Data"" Value=""M 3 5 L 11 5 L 7 11 Z""/>
                                <Setter TargetName=""Arrow"" Property=""Fill"" Value=""#9CA3AF""/>
                            </Trigger>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""Arrow"" Property=""Fill"" Value=""#60A5FA""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ═══════════ BUTTON STYLE ═══════════ -->
        <Style TargetType=""Button"">
            <Setter Property=""Background"">
                <Setter.Value>
                    <LinearGradientBrush StartPoint=""0,0"" EndPoint=""0,1"">
                        <GradientStop Color=""#2563EB"" Offset=""0""/>
                        <GradientStop Color=""#1D4ED8"" Offset=""1""/>
                    </LinearGradientBrush>
                </Setter.Value>
            </Setter>
            <Setter Property=""Foreground"" Value=""#F0F4FF""/>
            <Setter Property=""FontSize"" Value=""12""/>
            <Setter Property=""FontWeight"" Value=""SemiBold""/>
            <Setter Property=""BorderThickness"" Value=""0""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border Background=""{TemplateBinding Background}""
                            BorderBrush=""#3B5FCC"" BorderThickness=""1""
                            CornerRadius=""6"" Padding=""12,0"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter Property=""Background"">
                                    <Setter.Value>
                                        <LinearGradientBrush StartPoint=""0,0"" EndPoint=""0,1"">
                                            <GradientStop Color=""#3B82F6"" Offset=""0""/>
                                            <GradientStop Color=""#2563EB"" Offset=""1""/>
                                        </LinearGradientBrush>
                                    </Setter.Value>
                                </Setter>
                            </Trigger>
                            <Trigger Property=""IsPressed"" Value=""True"">
                                <Setter Property=""Background"">
                                    <Setter.Value>
                                        <LinearGradientBrush StartPoint=""0,0"" EndPoint=""0,1"">
                                            <GradientStop Color=""#1D4ED8"" Offset=""0""/>
                                            <GradientStop Color=""#1E3A8A"" Offset=""1""/>
                                        </LinearGradientBrush>
                                    </Setter.Value>
                                </Setter>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ═══════════ SCROLLBAR ═══════════ -->
        <Style TargetType=""ScrollBar"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""Width"" Value=""8""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ScrollBar"">
                        <Grid>
                            <Border Background=""#1A1C21"" CornerRadius=""4""/>
                            <Track Name=""PART_Track"" IsDirectionReversed=""true"">
                                <Track.Thumb>
                                    <Thumb>
                                        <Thumb.Style>
                                            <Style TargetType=""Thumb"">
                                                <Setter Property=""Template"">
                                                    <Setter.Value>
                                                        <ControlTemplate TargetType=""Thumb"">
                                                            <Border Background=""#3A3D45"" CornerRadius=""4"" Margin=""1""/>
                                                        </ControlTemplate>
                                                    </Setter.Value>
                                                </Setter>
                                            </Style>
                                        </Thumb.Style>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <!-- ═══════════ TITLE BAR BUTTON ═══════════ -->
        <Style x:Key=""TitleBarBtn"" TargetType=""Button"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""Foreground"" Value=""#9CA3AF""/>
            <Setter Property=""BorderThickness"" Value=""0""/>
            <Setter Property=""Width"" Value=""46""/>
            <Setter Property=""Height"" Value=""36""/>
            <Setter Property=""FontSize"" Value=""13""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border Name=""bg"" Background=""{TemplateBinding Background}"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""bg"" Property=""Background"" Value=""#2A2D35""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style x:Key=""TitleBarCloseBtn"" TargetType=""Button"" BasedOn=""{StaticResource TitleBarBtn}"">
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border Name=""bg"" Background=""{TemplateBinding Background}"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""bg"" Property=""Background"" Value=""#C42B1C""/>
                                <Setter Property=""Foreground"" Value=""White""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>
    
    <!-- MAIN WRAPPER -->
    <Border Background=""#181A1F"" CornerRadius=""10"" BorderBrush=""#2A2D33"" BorderThickness=""1"">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height=""36""/>
                <RowDefinition Height=""*""/>
            </Grid.RowDefinitions>

            <!-- ═══ CUSTOM TITLE BAR ═══ -->
            <Grid Grid.Row=""0"" Name=""TitleBar"">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width=""*""/>
                    <ColumnDefinition Width=""Auto""/>
                </Grid.ColumnDefinitions>

                <!-- Title text -->
                <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"" Margin=""14,0,0,0"">
                    <TextBlock Text=""⬡"" Foreground=""#3B82F6"" FontSize=""14"" Margin=""0,0,8,0"" VerticalAlignment=""Center""/>
                    <TextBlock Text=""Quản lý Chi tiết Trích"" FontSize=""12"" Foreground=""#9CA3AF"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
                </StackPanel>

                <!-- Window control buttons -->
                <StackPanel Grid.Column=""1"" Orientation=""Horizontal"">
                    <Button Name=""BtnMinimize"" Content=""─"" Style=""{StaticResource TitleBarBtn}""/>
                    <Button Name=""BtnMaximize"" Content=""☐"" Style=""{StaticResource TitleBarBtn}""/>
                    <Button Name=""BtnClose"" Content=""✕"" Style=""{StaticResource TitleBarCloseBtn}""/>
                </StackPanel>
            </Grid>

            <!-- ═══ CONTENT ═══ -->
            <Grid Grid.Row=""1"" Margin=""16,4,16,16"">
                <Grid.RowDefinitions>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""*""/>
                    <RowDefinition Height=""Auto""/>
                </Grid.RowDefinitions>

                <!-- Header -->
                <Border Grid.Row=""0"" Margin=""0,0,0,10"">
                    <TextBlock Text=""CHI TIẾT TRÍCH"" FontSize=""13"" FontWeight=""Bold"" Foreground=""#6B7280""/>
                </Border>

                <!-- TreeView Container -->
                <Border Grid.Row=""1"" Background=""#1F2127"" CornerRadius=""10""
                    BorderBrush=""#2A2D33"" BorderThickness=""1"" Padding=""8"">
                    <TreeView Name=""MainTreeView"" Background=""Transparent"" BorderThickness=""0""
                        ScrollViewer.HorizontalScrollBarVisibility=""Disabled"">
                        <TreeView.ItemTemplate>
                            <HierarchicalDataTemplate ItemsSource=""{Binding Children}"">
                                <TextBlock Text=""{Binding Title}"" FontSize=""12.5""
                                    FontWeight=""SemiBold"" Foreground=""#D1D5DB""
                                    TextTrimming=""CharacterEllipsis""/>
                            </HierarchicalDataTemplate>
                        </TreeView.ItemTemplate>
                    </TreeView>
                </Border>

                <!-- Footer -->
                <Grid Grid.Row=""2"" Margin=""0,10,0,0"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""Auto""/>
                        <ColumnDefinition Width=""Auto""/>
                        <ColumnDefinition Width=""*""/>
                    </Grid.ColumnDefinitions>
                    
                    <Button Name=""BtnRefresh"" Content=""Làm mới"" Width=""100"" Height=""32"" Grid.Column=""0"" Margin=""0,0,8,0""/>
                    <Button Name=""BtnGoTo"" Content=""Đi đến"" Width=""100"" Height=""32"" Grid.Column=""1""/>
                    <TextBlock Text=""Double-click để zoom nhanh"" Foreground=""#6B7280"" FontSize=""11""
                        VerticalAlignment=""Center"" HorizontalAlignment=""Right"" Grid.Column=""2""/>
                </Grid>
            </Grid>
        </Grid>
    </Border>
</Window>";

            // Parse XAML
            Window window = (Window)XamlReader.Parse(xaml);
            
            // Apply properties to current instance
            // WindowStyle và AllowsTransparency phải set TRƯỚC khi window handle được tạo
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Title = window.Title;
            this.Width = window.Width;
            this.Height = window.Height;
            this.WindowStartupLocation = window.WindowStartupLocation;
            this.ResizeMode = window.ResizeMode;
            this.Background = window.Background;
            this.Foreground = window.Foreground;
            this.FontFamily = window.FontFamily;
            this.Resources = window.Resources;
            this.Content = window.Content;

            // Find controls
            treeView = (TreeView)this.FindName("MainTreeView");
            if (treeView == null) 
            {
                treeView = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "MainTreeView") as TreeView;
            }
            
            btnRefresh = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnRefresh") as Button;
            btnGoTo = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnGoTo") as Button;

            // Title bar events
            var titleBar = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "TitleBar") as Grid;
            if (titleBar != null)
            {
                titleBar.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    }
                    else
                    {
                        this.DragMove();
                    }
                };
            }
            
            var btnMin = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnMinimize") as Button;
            var btnMax = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnMaximize") as Button;
            var btnCloseWin = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnClose") as Button;
            
            if (btnMin != null) btnMin.Click += (s, e) => this.WindowState = WindowState.Minimized;
            if (btnMax != null) btnMax.Click += (s, e) => this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            if (btnCloseWin != null) btnCloseWin.Click += (s, e) => this.Close();

            // Content events
            if (btnRefresh != null) btnRefresh.Click += (s, e) => RefreshData();
            if (btnGoTo != null) btnGoTo.Click += (s, e) => GoToSelectedNode();
            
            if (treeView != null) 
            {
                treeView.MouseDoubleClick += (s, e) => GoToSelectedNode();
            }
        }

        private void RefreshData()
        {
            if (treeView == null) return;
            
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var mappings = Callout.Commands.CalloutCommand.GetCalloutMappings(doc.Database);
            if (mappings.Count == 0)
            {
                treeView.ItemsSource = new List<CalloutNodeData> { new CalloutNodeData { Title = "Chưa có dữ liệu trích xuất nào trong bản vẽ này." } };
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
                            string bName = blk.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(blk.DynamicBlockTableRecord, OpenMode.ForRead)).Name : blk.Name;
                            if (bName.Equals(Callout.Services.CalloutConfig.TitleBlockName, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    string sheetNo = GetAttributeValue(tr, blk, Callout.Services.CalloutConfig.SheetNumberTag);
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
                    List<Callout.Commands.CalloutBubblePair> pairs = kvp.Value;

                    if (sourceId.IsErased || sourceId.IsNull) continue;
                    BlockReference sourceBlk = tr.GetObject(sourceId, OpenMode.ForRead) as BlockReference;
                    if (sourceBlk == null || sourceBlk.IsErased) continue;

                    string bName = sourceBlk.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(sourceBlk.DynamicBlockTableRecord, OpenMode.ForRead)).Name : sourceBlk.Name;
                    
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
                            string viewNum = GetAttributeValue(tr, titleBubble, "VIEWNUMBER") ?? "?";
                            string detailSheet = GetAttributeValue(tr, titleBubble, "SHEETNUMBER");
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
            
            treeView.ItemsSource = rootNodes;
            
            // Expand all by default — kế thừa Style gốc từ Resources, chỉ thêm IsExpanded
            Style baseStyle = treeView.FindResource(typeof(TreeViewItem)) as Style;
            Style expandedStyle = new Style(typeof(TreeViewItem), baseStyle);
            expandedStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, true));
            treeView.ItemContainerStyle = expandedStyle;
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

        private string GetAttributeValue(Transaction tr, BlockReference blockRef, string tag)
        {
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (attRef != null && attRef.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return attRef.TextString;
                }
            }
            return null;
        }

        private void GoToSelectedNode()
        {
            var data = treeView.SelectedItem as CalloutNodeData;
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
