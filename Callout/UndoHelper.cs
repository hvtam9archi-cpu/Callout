using Autodesk.AutoCAD.ApplicationServices;

namespace Callout.Logic
{
    /// <summary>
    /// Nhóm nhiều Transaction trong 1 lệnh thành 1 bước Undo duy nhất.
    /// Gọi Begin() ở đầu lệnh và End() ở cuối để Ctrl+Z hoàn tác toàn bộ.
    /// Sử dụng Editor.Command() đồng bộ thay vì SendStringToExecute bất đồng bộ.
    /// </summary>
    public static class UndoHelper
    {
        public static void Begin(Document doc)
        {
            if (doc == null) return;
            try
            {
                doc.Editor.Command("_.UNDO", "_Begin");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout Undo Begin] {ex}");
            }
        }

        public static void End(Document doc)
        {
            if (doc == null) return;
            try
            {
                doc.Editor.Command("_.UNDO", "_End");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout Undo End] {ex}");
            }
        }
    }
}
