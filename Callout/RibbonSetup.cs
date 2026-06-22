using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Callout.Services;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Callout.RibbonSetup))]

namespace Callout
{
    /// <summary>
    /// Entry Point: Khởi tạo Plugin, đăng ký Ribbon và các sự kiện hệ thống.
    /// Theo workflow /cad-architecture: IExtensionApplication + Idle + SystemVariableChanged.
    /// </summary>
    public class RibbonSetup : IExtensionApplication
    {
        private const string TabId = "TH_TOOLS_TAB";
        private const string TabTitle = "TH Tools";

        public void Initialize()
        {
            // Khởi tạo Watcher theo dõi thay đổi BlockReference/Attribute
            CalloutWatcher.Initialize();

            // Đăng ký sự kiện chờ Ribbon sẵn sàng
            Application.Idle += Application_Idle;
            Application.SystemVariableChanged += Application_SystemVariableChanged;
        }

        public void Terminate()
        {
            CalloutWatcher.Terminate();
            Application.Idle -= Application_Idle;
            Application.SystemVariableChanged -= Application_SystemVariableChanged;
        }

        private void Application_Idle(object sender, EventArgs e)
        {
            if (ComponentManager.Ribbon != null)
            {
                Application.Idle -= Application_Idle;
                CreateRibbon();
            }
        }

        private void Application_SystemVariableChanged(object sender, SystemVariableChangedEventArgs e)
        {
            if (e.Name.Equals("WSCURRENT", StringComparison.OrdinalIgnoreCase) && ComponentManager.Ribbon != null)
            {
                CreateRibbon();
            }
        }

        private void CreateRibbon()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;

                RibbonTab tab = null;
                foreach (RibbonTab existingTab in ribbon.Tabs)
                {
                    if (existingTab.Id == TabId)
                    {
                        tab = existingTab;
                        break;
                    }
                }

                if (tab == null)
                {
                    tab = new RibbonTab
                    {
                        Title = TabTitle,
                        Id = TabId
                    };
                    ribbon.Tabs.Add(tab);
                }

                string panelId = "CALLOUT_PANEL";
                foreach (RibbonPanel existingPanel in tab.Panels)
                {
                    if (existingPanel.Source.Id == panelId || existingPanel.Source.Title == "Chi tiết Trích")
                        return; // Panel đã tồn tại
                }

                RibbonPanelSource panelSource = new RibbonPanelSource { Title = "Chi tiết Trích", Id = panelId };

                // Row 1: Trích Chi Tiết (CT) + Cấu Hình (CT2)
                RibbonRow row1 = new RibbonRow();
                row1.Items.Add(CreateButton("CT", "Trích Chi Tiết", "Trích chi tiết gần (CT)", RibbonItemSize.Standard));
                row1.Items.Add(CreateButton("CT2", "Cấu Hình", "Thiết lập Block Khung (CT2)", RibbonItemSize.Standard));
                panelSource.Items.Add(row1);

                // Row 2: Trích Xa (CT1) + Quản Lý (CTS)
                RibbonRow row2 = new RibbonRow();
                row2.Items.Add(CreateButton("CT1", "Trích Xa", "Trích chi tiết xa (CT1)", RibbonItemSize.Standard));
                row2.Items.Add(CreateButton("CTS", "Quản Lý", "Quản lý chi tiết trích (CTS)", RibbonItemSize.Standard));
                panelSource.Items.Add(row2);

                RibbonPanel panel = new RibbonPanel { Source = panelSource };
                tab.Panels.Add(panel);

                tab.IsActive = true; 
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[Callout] Lỗi tạo Ribbon: {ex.Message}");
            }
        }

        private RibbonButton CreateButton(string commandName, string text, string tooltip, RibbonItemSize size)
        {
            // Trích xuất các chữ cái đầu tiên làm icon text
            string iconText = commandName.Length <= 3 ? commandName : commandName.Substring(0, 2);

            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Image = GetIcon(iconText, 16),
                LargeImage = GetIcon(iconText, 32),
                CommandParameter = commandName,
                CommandHandler = new RibbonCommandHandler(),
                Size = size,
                ToolTip = tooltip
            };
        }

        private System.Windows.Media.ImageSource GetIcon(string text, int size)
        {
            using (var bmp = new System.Drawing.Bitmap(size, size))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                // Màu nền Dark Theme Accent
                g.Clear(System.Drawing.Color.FromArgb(37, 99, 235)); 
                
                // Căn giữa text
                var format = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center
                };

                // Font chữ
                using (var font = new System.Drawing.Font("Segoe UI", size * 0.35f, System.Drawing.FontStyle.Bold))
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                {
                    g.DrawString(text, font, brush, new System.Drawing.RectangleF(0, 0, size, size), format);
                }

                // Chuyển sang BitmapImage cho WPF
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = ms;
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    return bi;
                }
            }
        }
    }

    /// <summary>
    /// Handler chung cho các nút Ribbon — thực thi lệnh AutoCAD.
    /// CommandParameter được set là string (tên lệnh như "CT", "CT1").
    /// </summary>
    public class RibbonCommandHandler : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            string commandName = parameter as string;
            if (!string.IsNullOrEmpty(commandName))
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.SendStringToExecute($"{commandName} ", true, false, true);
                }
            }
        }
    }
}
