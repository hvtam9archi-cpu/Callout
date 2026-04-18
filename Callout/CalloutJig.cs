using System;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Callout.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Callout.Jigs
{
    public class CalloutJig : DrawJig
    {
        public BlockReference JigRef { get; private set; }
        public Point3d CurrentPosition { get; private set; }
        public Matrix3d MathTransform { get; private set; }

        private readonly Point3d _basePoint;
        private readonly Extents3d _originalExtents;
        private readonly bool _isFarCallout;

        public CalloutJig(BlockReference jigRef, Point3d basePoint, Extents3d origExt, bool isFarCallout = false)
        {
            JigRef = jigRef;
            _basePoint = basePoint;
            _originalExtents = origExt;
            CurrentPosition = basePoint;
            _isFarCallout = isFarCallout;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptPointOptions options = new JigPromptPointOptions("\nThay đổi Tỷ lệ: ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted | UserInputControls.GovernedByOrthoMode,
                UseBasePoint = true,
                BasePoint = _basePoint
            };

            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Cancel) return SamplerStatus.Cancel;

            if (JigInputHandler.ScaleChanged || result.Value.DistanceTo(CurrentPosition) > 0.001)
            {
                CurrentPosition = result.Value;
                JigInputHandler.ScaleChanged = false;
                return SamplerStatus.OK;
            }

            return SamplerStatus.NoChange;
        }

        protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
        {
            JigRef.Position = CurrentPosition;
            JigRef.ScaleFactors = new Scale3d(JigInputHandler.CurrentScale);

            draw.Geometry.Draw(JigRef);

            MathTransform = Matrix3d.Scaling(JigInputHandler.CurrentScale, CurrentPosition) * Matrix3d.Displacement(_basePoint.GetVectorTo(CurrentPosition));

            double viewSize = (double)AcadApp.GetSystemVariable("VIEWSIZE");
            double visualDotSize = viewSize * 0.005;

            if (!_isFarCallout)
            {
                var leaderEntities = CalloutGeometryService.CreateSmartLeader(_originalExtents, MathTransform, visualDotSize);
                foreach (var ent in leaderEntities)
                {
                    draw.Geometry.Draw(ent);
                    ent.Dispose();
                }
            }

            using (DBText contextText = new DBText())
            {
                contextText.Position = CurrentPosition + new Vector3d(viewSize * 0.03, viewSize * 0.05, 0);
                contextText.Height = viewSize * 0.02;
                contextText.TextString = $"Tỷ lệ: {JigInputHandler.CurrentScale}x";
                contextText.ColorIndex = 2;
                draw.Geometry.Draw(contextText);
            }

            return true;
        }
    }

    public static class JigInputHandler
    {
        public static double CurrentScale = 1.0;
        public static bool ScaleChanged = false;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        public static void Start()
        {
            CurrentScale = 1.0;
            ScaleChanged = false;
            AcadApp.PreTranslateMessage += OnPreTranslateMessage;
        }

        public static void Stop()
        {
            AcadApp.PreTranslateMessage -= OnPreTranslateMessage;
        }

        private static void OnPreTranslateMessage(object sender, Autodesk.AutoCAD.ApplicationServices.PreTranslateMessageEventArgs e)
        {
            // NGUYÊN TẮC: Global Try Catch Catch để đảm bảo An toàn trên tầng Host
            try
            {
                if (e.Message.message == 0x0100)
                {
                    int vkCode = (int)e.Message.wParam;
                    bool handled = false;

                    if (vkCode == 38 || vkCode == 87 || vkCode == 107 || vkCode == 187) // Up/W/+
                    {
                        CurrentScale += 1.0;
                        handled = true;
                    }
                    else if (vkCode == 40 || vkCode == 83 || vkCode == 109 || vkCode == 189) // Down/S/-
                    {
                        if (CurrentScale > 1.0) CurrentScale -= 1.0;
                        handled = true;
                    }

                    if (handled)
                    {
                        ScaleChanged = true;
                        e.Handled = true;

                        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n>> Tỷ lệ trích xuất hiện tại: {CurrentScale}x");

                        if (GetCursorPos(out POINT point))
                        {
                            SetCursorPos(point.X + 1, point.Y);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[ERROR PreTranslateMessage]: {ex.Message}");
            }
        }
    }
}
