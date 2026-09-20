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
            try
            {
                // Khởi tạo Watcher theo dõi thay đổi BlockReference/Attribute
                CalloutWatcher.Initialize();

                // Đăng ký sự kiện chờ Ribbon sẵn sàng
                Application.Idle += Application_Idle;
                Application.SystemVariableChanged += Application_SystemVariableChanged;
            }
            catch (System.Exception ex)
            {
                WriteError("Initialize", ex);
                Application.Idle -= Application_Idle;
                Application.SystemVariableChanged -= Application_SystemVariableChanged;
                CalloutWatcher.Terminate();
            }
        }

        public void Terminate()
        {
            CalloutWatcher.Terminate();
            Callout.Logic.CalloutCoreLogic.Terminate();
            Application.Idle -= Application_Idle;
            Application.SystemVariableChanged -= Application_SystemVariableChanged;
        }

        private void Application_Idle(object sender, EventArgs e)
        {
            try
            {
                if (ComponentManager.Ribbon != null && CreateRibbon())
                {
                    Application.Idle -= Application_Idle;
                }
            }
            catch (System.Exception ex)
            {
                WriteError("Ribbon Idle", ex);
            }
        }

        private void Application_SystemVariableChanged(object sender, SystemVariableChangedEventArgs e)
        {
            try
            {
                if (e.Name.Equals("WSCURRENT", StringComparison.OrdinalIgnoreCase) && ComponentManager.Ribbon != null)
                {
                    CreateRibbon();
                }
            }
            catch (System.Exception ex)
            {
                WriteError("SystemVariableChanged", ex);
            }
        }

        private bool CreateRibbon()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return false;

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
                    if (existingPanel.Source.Id == panelId || existingPanel.Source.Title == "Detail Callout" || existingPanel.Source.Title == "Chi tiết Trích")
                        return true; // Panel đã tồn tại
                }

                RibbonPanelSource panelSource = new RibbonPanelSource { Title = "Detail Callout", Id = panelId };

                // Xếp 4 button song song trong 1 RibbonRowPanel — Large = icon trên + tên dưới
                RibbonRowPanel rowPanel = new RibbonRowPanel();
                rowPanel.Items.Add(CreateButton("CT", "Detail Callout", "Detail Callout (CT)", RibbonItemSize.Large, "IconRibbon_DetailCallout_32px.ico"));
                rowPanel.Items.Add(CreateButton("CT1", "Detail Callout\nBubble", "Callout Bubble (CT1)", RibbonItemSize.Large, "IconRibbon_CalloutBubble_32px.ico"));
                rowPanel.Items.Add(CreateButton("CTS", "Manage\nCallout", "Manage (CTS)", RibbonItemSize.Large, "IconRibbon_CalloutManage_32px.ico"));
                rowPanel.Items.Add(CreateButton("CT2", "Setting\nCallout", "Settings (CT2)", RibbonItemSize.Large, "IconRibbon_SettingsCallout_32px.ico"));
                panelSource.Items.Add(rowPanel);

                RibbonPanel panel = new RibbonPanel { Source = panelSource };
                tab.Panels.Add(panel);

                tab.IsActive = true; 
                return true;
            }
            catch (System.Exception ex)
            {
                WriteError("CreateRibbon", ex);
                return false;
            }
        }

        private static void WriteError(string operation, System.Exception exception)
        {
            try
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[Callout - {operation}] {exception.Message}");
            }
            catch (System.Exception fallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout - {operation}] {exception}\nFallback: {fallbackException}");
            }
        }

        private RibbonButton CreateButton(string commandName, string text, string tooltip, RibbonItemSize size, string iconFileName = null)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Image = string.IsNullOrEmpty(iconFileName) ? GetIcon(commandName, 16) : LoadIcon(iconFileName, 16),
                LargeImage = string.IsNullOrEmpty(iconFileName) ? GetIcon(commandName, 32) : LoadIcon(iconFileName, 32),
                CommandParameter = commandName,
                CommandHandler = new RibbonCommandHandler(),
                Size = size,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                ToolTip = tooltip
            };
        }

        /// <summary>
        /// Load icon từ thư mục Resource (cạnh assembly), decode đúng kích thước để tránh crop.
        /// </summary>
        private System.Windows.Media.ImageSource LoadIcon(string fileName, int decodeSize = 32)
        {
            try
            {
                string assemblyDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(assemblyDir, "Resource", fileName);
                if (System.IO.File.Exists(path))
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.DecodePixelWidth = decodeSize;
                    bi.DecodePixelHeight = decodeSize;
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    return bi;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout Ribbon] Không tải được icon {fileName}: {ex}");
            }
            return null;
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
        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            try
            {
                // AutoCAD có thể truyền CommandParameter (string) hoặc chính RibbonButton
                string commandName = null;
                if (parameter is RibbonButton button)
                    commandName = button.CommandParameter as string;
                else if (parameter is string str)
                    commandName = str;

                if (!string.IsNullOrEmpty(commandName))
                {
                    Document doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc != null)
                    {
                        string cleanCmd = commandName.Replace("\x03", "").Trim();
                        if (!string.IsNullOrEmpty(cleanCmd))
                        {
                            doc.SendStringToExecute("\x1B\x1B", true, false, false);
                            doc.SendStringToExecute(cleanCmd + "\n", true, false, false);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                try
                {
                    Application.DocumentManager.MdiActiveDocument?.Editor
                        .WriteMessage($"\n[Callout Ribbon Command] {ex.Message}");
                }
                catch (System.Exception fallbackException)
                {
                    System.Diagnostics.Debug.WriteLine($"[Callout Ribbon Command] {ex}\nFallback: {fallbackException}");
                }
            }
        }
    }
}
