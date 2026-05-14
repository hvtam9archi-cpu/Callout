using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
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
            
            TxtBlockName.Text = Callout.Services.CalloutConfig.TitleBlockName;
            
            if (!string.IsNullOrEmpty(Callout.Services.CalloutConfig.SheetNumberTag))
            {
                CbxAttributes.Items.Add(Callout.Services.CalloutConfig.SheetNumberTag);
                CbxAttributes.SelectedItem = Callout.Services.CalloutConfig.SheetNumberTag;
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
                        foreach (ObjectId attId in blk.AttributeCollection)
                        {
                            if (attId.IsErased) continue;
                            AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (attRef != null)
                            {
                                CbxAttributes.Items.Add(attRef.Tag);
                            }
                        }

                        if (CbxAttributes.Items.Count > 0)
                            CbxAttributes.SelectedIndex = 0;
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
            if (string.IsNullOrEmpty(TxtBlockName.Text) || CbxAttributes.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn Block và Attribute!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Callout.Services.CalloutConfig.TitleBlockName = TxtBlockName.Text;
            Callout.Services.CalloutConfig.SheetNumberTag = CbxAttributes.SelectedItem.ToString();
            
            Callout.Services.CalloutWatcher.TriggerManualUpdate();

            this.DialogResult = true;
            this.Close();
        }
    }
}
