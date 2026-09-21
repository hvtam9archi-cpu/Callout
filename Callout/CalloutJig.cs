using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Callout.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Callout.Jigs
{
    public sealed class CalloutJig : DrawJig, IDisposable
    {
        public Point3d CurrentPosition { get; private set; }
        public Matrix3d MathTransform { get; private set; }

        private BlockReference _previewBlock;
        private Curve _previewBoundary;
        private List<AttributeReference> _previewAttributes;
        private readonly Point2dCollection _clipBoundaryPoints;
        private readonly Point3d _basePoint;
        private readonly Extents3d _originalExtents;
        private readonly bool _isFarCallout;
        private bool _disposed;

        public CalloutJig(
            BlockReference previewBlock,
            Curve previewBoundary,
            List<AttributeReference> previewAttributes,
            Point2dCollection clipBoundaryPoints,
            Point3d basePoint,
            Extents3d originalExtents,
            bool isFarCallout = false)
        {
            _previewBlock = previewBlock ?? throw new ArgumentNullException(nameof(previewBlock));
            _previewBoundary = previewBoundary ?? throw new ArgumentNullException(nameof(previewBoundary));
            _previewAttributes = previewAttributes ?? new List<AttributeReference>();
            _clipBoundaryPoints = clipBoundaryPoints ?? throw new ArgumentNullException(nameof(clipBoundaryPoints));
            if (_clipBoundaryPoints.Count < 3)
            {
                throw new ArgumentException("Clip boundary must contain at least three points.", nameof(clipBoundaryPoints));
            }

            _basePoint = basePoint;
            _originalExtents = originalExtents;
            CurrentPosition = basePoint;
            _isFarCallout = isFarCallout;
            MathTransform = Matrix3d.Identity;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            try
            {
                JigPromptPointOptions options = new JigPromptPointOptions("\nThay đổi Tỷ lệ: ")
                {
                    UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted | UserInputControls.GovernedByOrthoMode,
                    UseBasePoint = true,
                    BasePoint = _basePoint
                };

                PromptPointResult result = prompts.AcquirePoint(options);
                if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;

                if (JigInputHandler.ScaleChanged || result.Value.DistanceTo(CurrentPosition) > 0.001)
                {
                    CurrentPosition = result.Value;
                    JigInputHandler.ScaleChanged = false;
                    return SamplerStatus.OK;
                }

                return SamplerStatus.NoChange;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout] Jig sampler failed: {ex}");
                return SamplerStatus.Cancel;
            }
        }

        protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
        {
            try
            {
                if (_disposed || _previewBlock == null || _previewBoundary == null)
                {
                    return false;
                }

                double scale = JigInputHandler.CurrentScale;
                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0)
                {
                    return false;
                }

                MathTransform = Matrix3d.Scaling(scale, CurrentPosition) * Matrix3d.Displacement(_basePoint.GetVectorTo(CurrentPosition));

                bool modelTransformPushed = draw.Geometry.PushModelTransform(MathTransform);
                if (!modelTransformPushed)
                {
                    return false;
                }

                ClipBoundary clipBoundary = null;
                bool clipBoundaryPushed = false;
                try
                {
                    clipBoundary = CreateClipBoundary();
                    clipBoundaryPushed = draw.Geometry.PushClipBoundary(clipBoundary);
                    if (clipBoundaryPushed)
                    {
                        draw.Geometry.Draw(_previewBlock);
                        foreach (AttributeReference attribute in _previewAttributes)
                        {
                            if (attribute != null && !attribute.IsDisposed)
                            {
                                draw.Geometry.Draw(attribute);
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[Callout] PushClipBoundary failed; full block preview was suppressed.");
                    }
                }
                finally
                {
                    try
                    {
                        draw.Geometry.PopModelTransform();
                    }
                    finally
                    {
                        try
                        {
                            if (clipBoundaryPushed)
                            {
                                draw.Geometry.PopClipBoundary();
                            }
                        }
                        finally
                        {
                            clipBoundary?.Dispose();
                        }
                    }
                }

                bool boundaryTransformPushed = draw.Geometry.PushModelTransform(MathTransform);
                if (!boundaryTransformPushed)
                {
                    return false;
                }

                try
                {
                    draw.Geometry.Draw(_previewBoundary);
                }
                finally
                {
                    draw.Geometry.PopModelTransform();
                }

                double viewSize = (double)AcadApp.GetSystemVariable("VIEWSIZE");
                if (double.IsNaN(viewSize) || double.IsInfinity(viewSize) || viewSize <= 0.0)
                {
                    return true;
                }

                double visualDotSize = viewSize * 0.005;

                if (!_isFarCallout)
                {
                    var leaderEntities = CalloutGeometryService.CreateSmartLeader(_originalExtents, MathTransform, visualDotSize);
                    try
                    {
                        foreach (var ent in leaderEntities)
                        {
                            if (ent == null || ent.IsDisposed) continue;
                            draw.Geometry.Draw(ent);
                        }
                    }
                    finally
                    {
                        foreach (var ent in leaderEntities)
                        {
                            if (ent != null && !ent.IsDisposed) ent.Dispose();
                        }
                    }
                }

                using (DBText contextText = new DBText())
                {
                    contextText.Position = CurrentPosition + new Vector3d(viewSize * 0.03, viewSize * 0.05, 0);
                    contextText.Height = viewSize * 0.02;
                    contextText.TextString = $"Tỷ lệ: {scale}x";
                    contextText.ColorIndex = 2;
                    draw.Geometry.Draw(contextText);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Callout] Jig preview failed: {ex}");
                return false;
            }
        }

        private ClipBoundary CreateClipBoundary()
        {
            ClipBoundary clipBoundary = new ClipBoundary();
            try
            {
                clipBoundary.NormalVector = Vector3d.ZAxis;
                clipBoundary.Point = Point3d.Origin;
                clipBoundary.TransformToClipSpace = Matrix3d.Identity;
                clipBoundary.TransformInverseBlockRefXForm = _previewBlock.BlockTransform.Inverse();
                clipBoundary.ClippingFront = false;
                clipBoundary.ClippingBack = false;
                clipBoundary.FrontClipZ = 0.0;
                clipBoundary.BackClipZ = 0.0;
                clipBoundary.DrawBoundary = false;
                clipBoundary.SetAptPoints(_clipBoundaryPoints);
                return clipBoundary;
            }
            catch
            {
                clipBoundary.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_previewBlock != null && !_previewBlock.IsDisposed)
            {
                _previewBlock.Dispose();
            }
            _previewBlock = null;

            if (_previewBoundary != null && !_previewBoundary.IsDisposed)
            {
                _previewBoundary.Dispose();
            }
            _previewBoundary = null;

            if (_previewAttributes != null)
            {
                foreach (AttributeReference attribute in _previewAttributes)
                {
                    if (attribute != null && !attribute.IsDisposed)
                    {
                        attribute.Dispose();
                    }
                }
                _previewAttributes.Clear();
                _previewAttributes = null;
            }
        }
    }

    public static class JigInputHandler
    {
        public static double CurrentScale = 1.0;
        public static bool ScaleChanged = false;
        private static bool _started = false;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        public static void Start()
        {
            Stop();
            CurrentScale = 1.0;
            ScaleChanged = false;
            AcadApp.PreTranslateMessage += OnPreTranslateMessage;
            _started = true;
        }

        public static void Stop()
        {
            if (!_started) return;
            AcadApp.PreTranslateMessage -= OnPreTranslateMessage;
            _started = false;
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
