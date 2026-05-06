using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Callout.UI
{
    public class CalloutConfigWindow : Window
    {
        private TextBox txtBlockName;
        private ComboBox cbxAttributes;
        private Button btnSelectBlock;
        private Button btnSave;

        public CalloutConfigWindow()
        {
            InitializeUI();
            
            txtBlockName.Text = Callout.Services.CalloutConfig.TitleBlockName;
            
            if (!string.IsNullOrEmpty(Callout.Services.CalloutConfig.SheetNumberTag))
            {
                cbxAttributes.Items.Add(Callout.Services.CalloutConfig.SheetNumberTag);
                cbxAttributes.SelectedItem = Callout.Services.CalloutConfig.SheetNumberTag;
            }
        }

        private void InitializeUI()
        {
            string xaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Title=""Thiết lập Khung Bản Vẽ (CT2)"" Width=""420"" Height=""280""
        WindowStartupLocation=""CenterScreen"" ResizeMode=""NoResize""
        WindowStyle=""None"" AllowsTransparency=""True""
        Background=""Transparent"" Foreground=""#E8EAED"" FontFamily=""Segoe UI"">
    <Window.Resources>
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

        <!-- ═══════════ TEXTBOX STYLE ═══════════ -->
        <Style TargetType=""TextBox"">
            <Setter Property=""Background"" Value=""#2B2D32""/>
            <Setter Property=""Foreground"" Value=""#D1D5DB""/>
            <Setter Property=""BorderBrush"" Value=""#333640""/>
            <Setter Property=""BorderThickness"" Value=""1""/>
            <Setter Property=""Padding"" Value=""8,0""/>
            <Setter Property=""CaretBrush"" Value=""#D1D5DB""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""TextBox"">
                        <Border Background=""{TemplateBinding Background}""
                            BorderBrush=""{TemplateBinding BorderBrush}""
                            BorderThickness=""{TemplateBinding BorderThickness}""
                            CornerRadius=""6"">
                            <ScrollViewer x:Name=""PART_ContentHost"" Margin=""2,0""/>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ═══════════ COMBOBOX STYLE ═══════════ -->
        <Style TargetType=""ComboBox"">
            <Setter Property=""Background"" Value=""#2B2D32""/>
            <Setter Property=""Foreground"" Value=""#D1D5DB""/>
            <Setter Property=""BorderBrush"" Value=""#333640""/>
            <Setter Property=""BorderThickness"" Value=""1""/>
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
                    <TextBlock Text=""Thiết lập Khung Bản Vẽ"" FontSize=""12"" Foreground=""#9CA3AF"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
                </StackPanel>

                <!-- Window control buttons -->
                <StackPanel Grid.Column=""1"" Orientation=""Horizontal"">
                    <Button Name=""BtnCloseWin"" Content=""✕"" Style=""{StaticResource TitleBarCloseBtn}""/>
                </StackPanel>
            </Grid>

            <!-- ═══ CONTENT ═══ -->
            <Grid Grid.Row=""1"" Margin=""20,4,20,20"">
                <Grid.RowDefinitions>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""*""/>
                    <RowDefinition Height=""Auto""/>
                </Grid.RowDefinitions>
                
                <!-- Label: Block Khung -->
                <TextBlock Text=""Block Khung"" FontSize=""12"" Foreground=""#6B7280"" Grid.Row=""0"" Margin=""0,0,0,6""/>
                
                <!-- Block name + Select button -->
                <Grid Grid.Row=""1"" Margin=""0,0,0,16"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""*""/>
                        <ColumnDefinition Width=""Auto""/>
                    </Grid.ColumnDefinitions>
                    <TextBox Name=""TxtBlockName"" IsReadOnly=""True"" Height=""32"" VerticalContentAlignment=""Center""/>
                    <Button Name=""BtnSelectBlock"" Content=""Chọn Block"" Width=""100"" Height=""32"" Grid.Column=""1"" Margin=""8,0,0,0""/>
                </Grid>
                
                <!-- Label: Attribute -->
                <TextBlock Text=""Attribute làm Sheet Number"" FontSize=""12"" Foreground=""#6B7280"" Grid.Row=""2"" Margin=""0,0,0,6""/>
                
                <!-- ComboBox -->
                <ComboBox Name=""CbxAttributes"" Height=""32"" Grid.Row=""3"" Margin=""0,0,0,20""/>
                
                <!-- Save button -->
                <Button Name=""BtnSave"" Content=""Lưu Thiết Lập"" Width=""140"" Height=""36"" Grid.Row=""5"" HorizontalAlignment=""Center""/>
            </Grid>
        </Grid>
    </Border>
</Window>";

            Window window = (Window)XamlReader.Parse(xaml);
            
            this.Title = window.Title;
            this.Width = window.Width;
            this.Height = window.Height;
            this.WindowStartupLocation = window.WindowStartupLocation;
            this.ResizeMode = window.ResizeMode;
            this.WindowStyle = window.WindowStyle;
            this.AllowsTransparency = window.AllowsTransparency;
            this.Background = window.Background;
            this.Foreground = window.Foreground;
            this.FontFamily = window.FontFamily;
            this.Resources = window.Resources;
            this.Content = window.Content;

            txtBlockName = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "TxtBlockName") as TextBox;
            cbxAttributes = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "CbxAttributes") as ComboBox;
            btnSelectBlock = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnSelectBlock") as Button;
            btnSave = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnSave") as Button;

            // Title bar events
            var titleBar = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "TitleBar") as Grid;
            if (titleBar != null)
            {
                titleBar.MouseLeftButtonDown += (s, e) => { this.DragMove(); };
            }
            
            var btnCloseWin = LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "BtnCloseWin") as Button;
            if (btnCloseWin != null) btnCloseWin.Click += (s, e) => this.Close();

            // Content events
            if (btnSelectBlock != null) btnSelectBlock.Click += BtnSelectBlock_Click;
            if (btnSave != null) btnSave.Click += BtnSave_Click;
        }

        private void BtnSelectBlock_Click(object sender, RoutedEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            
            this.Hide();

            PromptEntityOptions peo = new PromptEntityOptions("\nChọn Block Attribute làm Block Khung: ");
            peo.SetRejectMessage("\nChỉ chọn BlockReference!");
            peo.AddAllowedClass(typeof(BlockReference), true);

            var res = ed.GetEntity(peo);
            if (res.Status == PromptStatus.OK)
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockReference blk = tr.GetObject(res.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (blk != null)
                    {
                        string bName = blk.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(blk.DynamicBlockTableRecord, OpenMode.ForRead)).Name : blk.Name;
                        txtBlockName.Text = bName;

                        cbxAttributes.Items.Clear();
                        foreach (ObjectId attId in blk.AttributeCollection)
                        {
                            if (attId.IsErased) continue;
                            AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (attRef != null)
                            {
                                cbxAttributes.Items.Add(attRef.Tag);
                            }
                        }

                        if (cbxAttributes.Items.Count > 0)
                            cbxAttributes.SelectedIndex = 0;
                        else
                            MessageBox.Show("Block được chọn không có Attribute nào!", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    tr.Commit();
                }
            }

            this.ShowDialog();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtBlockName.Text) || cbxAttributes.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn Block và Attribute!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Callout.Services.CalloutConfig.TitleBlockName = txtBlockName.Text;
            Callout.Services.CalloutConfig.SheetNumberTag = cbxAttributes.SelectedItem.ToString();
            
            Callout.Services.CalloutWatcher.TriggerManualUpdate();

            this.DialogResult = true;
            this.Close();
        }
    }
}
