using Autodesk.AutoCAD.ApplicationServices;

namespace Callout.Logic
{
    /// <summary>
    /// Nhóm nhiều Transaction trong 1 lệnh thành 1 bước Undo duy nhất.
    /// Gọi Begin() ở đầu lệnh và End() ở cuối để Ctrl+Z hoàn tác toàn bộ.
    /// </summary>
    public static class UndoHelper
    {
        public static void Begin(Document doc)
        {
            if (doc == null) return;
            doc.SendStringToExecute("_.UNDO _Begin ", false, false, false);
        }

        public static void End(Document doc)
        {
            if (doc == null) return;
            doc.SendStringToExecute("_.UNDO _End ", false, false, false);
        }
    }
}
