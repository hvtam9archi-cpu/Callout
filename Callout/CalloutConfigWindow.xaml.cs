using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Callout.UI
{
    public partial class CalloutConfigWindow : Window
    {
        public CalloutConfigWindow()
        {
            InitializeComponent();
            
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this)) return;

            LoadTitleIcon("IconRibbon_SettingsCallout_32px.ico");
            TxtBlockName.Text = Callout.Services.CalloutConfig.TitleBlockName;
            
            if (!string.IsNullOrEmpty(Callout.Services.CalloutConfig.SheetNumberTag))
            {
                CbxAttributes.Items.Add(Callout.Services.CalloutConfig.SheetNumberTag);
                CbxAttributes.SelectedItem = Callout.Services.CalloutConfig.SheetNumberTag;
            }

            if (!string.IsNullOrEmpty(Callout.Services.CalloutConfig.ScaleTag))
            {
                CbxScaleAttributes.Items.Add(Callout.Services.CalloutConfig.ScaleTag);
                CbxScaleAttributes.SelectedItem = Callout.Services.CalloutConfig.ScaleTag;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnSelectBlock_Click(object sender, RoutedEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            this.Hide();

            try
            {
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
                            TxtBlockName.Text = bName;

                            CbxAttributes.Items.Clear();
                            CbxScaleAttributes.Items.Clear();
                            foreach (ObjectId attId in blk.AttributeCollection)
                            {
                                if (attId.IsErased) continue;
                                AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (attRef != null)
                                {
                                    CbxAttributes.Items.Add(attRef.Tag);
                                    CbxScaleAttributes.Items.Add(attRef.Tag);
                                }
                            }

                            if (CbxAttributes.Items.Count > 0)
                            {
                                CbxAttributes.SelectedIndex = 0;
                                CbxScaleAttributes.SelectedIndex = 0;
                            }
                            else
                            {
                                MessageBox.Show("Block được chọn không có Attribute nào!", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR Chọn Block]: {ex.Message}");
            }
            finally
            {
                // Đảm bảo cửa sổ luôn hiện lại, kể cả khi có Exception
                this.Visibility = System.Windows.Visibility.Visible;
            }
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

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtBlockName.Text) || CbxAttributes.SelectedItem == null || CbxScaleAttributes.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn Block và các Attribute!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Callout.Services.CalloutConfig.TitleBlockName = TxtBlockName.Text;
            Callout.Services.CalloutConfig.SheetNumberTag = CbxAttributes.SelectedItem.ToString();
            Callout.Services.CalloutConfig.ScaleTag = CbxScaleAttributes.SelectedItem.ToString();
            
            Callout.Services.CalloutWatcher.TriggerManualUpdate();

            this.DialogResult = true;
            this.Close();
        }
    }
}
