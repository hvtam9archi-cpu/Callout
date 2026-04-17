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
		private readonly Extents3d _origExtents;

		public CalloutJig(BlockReference jigRef, Point3d basePoint, Extents3d origExt)
		{
			JigRef = jigRef;
			_basePoint = basePoint;
			_origExtents = origExt;
			CurrentPosition = basePoint;
		}

		protected override SamplerStatus Sampler(JigPrompts prompts)
		{
			JigPromptPointOptions opt = new JigPromptPointOptions("\n+/- Tỷ lệ): ")
			{
				UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted | UserInputControls.GovernedByOrthoMode,
				UseBasePoint = true,
				BasePoint = _basePoint
			};

			PromptPointResult res = prompts.AcquirePoint(opt);
			if (res.Status == PromptStatus.Cancel) return SamplerStatus.Cancel;

			if (JigInputHandler.ScaleChanged || res.Value.DistanceTo(CurrentPosition) > 0.001)
			{
				CurrentPosition = res.Value;
				JigInputHandler.ScaleChanged = false;
				return SamplerStatus.OK;
			}

			return SamplerStatus.NoChange;
		}

		protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
		{
			JigRef.Position = CurrentPosition;
			JigRef.ScaleFactors = new Scale3d(JigInputHandler.CurrentScale);

			// Vẽ khối trích xuất
			draw.Geometry.Draw(JigRef);

			// Tính ma trận thuần túy để dóng đường Leader
			MathTransform = Matrix3d.Scaling(JigInputHandler.CurrentScale, CurrentPosition) * Matrix3d.Displacement(_basePoint.GetVectorTo(CurrentPosition));

			// Vẽ đường nối
			using (Polyline leader = CalloutGeometryService.CreateSmartLeader(_origExtents, MathTransform))
			{
				draw.Geometry.Draw(leader);
			}

			// --- TÍNH NĂNG MỚI: HEADS-UP DISPLAY (Hiện Tỷ Lệ cạnh con trỏ) ---
			double viewSize = (double)AcadApp.GetSystemVariable("VIEWSIZE");
			using (DBText txt = new DBText())
			{
				// Căn lùi vị trí Text xéo xuống dưới một chút cho khỏi đè vào chuột
				txt.Position = CurrentPosition + new Vector3d(viewSize * 0.03, -viewSize * 0.03, 0);
				txt.Height = viewSize * 0.02; // Tự động to nhỏ phù hợp với màn hình
				txt.TextString = $"Ty le: {JigInputHandler.CurrentScale}x";
				txt.ColorIndex = 2; // Màu vàng
				draw.Geometry.Draw(txt);
			}

			return true;
		}
	}

	/// <summary>
	/// Native Hook chặn phím, đổi Tỷ Lệ và xuất Text ra Command Line
	/// </summary>
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
			if (e.Message.message == 0x0100) // WM_KEYDOWN
			{
				int vkCode = (int)e.Message.wParam;
				bool handled = false;

				if (vkCode == 38 || vkCode == 87 || vkCode == 107 || vkCode == 187)
				{
					CurrentScale += 1.0;
					handled = true;
				}
				else if (vkCode == 40 || vkCode == 83 || vkCode == 109 || vkCode == 189)
				{
					if (CurrentScale > 1.0) CurrentScale -= 1.0;
					handled = true;
				}

				if (handled)
				{
					ScaleChanged = true;
					e.Handled = true; // Tiêu hủy phím, không dính vào Command Line

					// Hiển thị ra Command Line
					try
					{
						var doc = AcadApp.DocumentManager.MdiActiveDocument;
						doc?.Editor.WriteMessage($"\n>> Tỷ lệ trích xuất hiện tại: {CurrentScale}x");
					}
					catch { }

					// Ních chuột 1 pixel để cập nhật hình ngay lập tức
					if (GetCursorPos(out POINT p)) SetCursorPos(p.X + 1, p.Y);
				}
			}
		}
	}
}