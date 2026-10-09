using System;
using System.IO;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SshManager;

public static class IconBuilder
{
	public static void Generate(string icoPath, string pngPath)
	{
			int[] sizes = new[] { 16, 32, 48, 64, 128, 256 };
			var pngStreams = new MemoryStream[sizes.Length];

			for (int i = 0; i < sizes.Length; i++)
			{
				int size = sizes[i];
				var rtb = RenderIcon(size);
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(rtb));
				pngStreams[i] = new MemoryStream();
				encoder.Save(pngStreams[i]);

				if (size == 256)
				{
					var pngEncoder = new PngBitmapEncoder();
					pngEncoder.Frames.Add(BitmapFrame.Create(rtb));
					using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write);
					pngEncoder.Save(fs);
				}
			}

			using var fsIco = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
			using var bw = new BinaryWriter(fsIco);

			bw.Write((ushort)0);   // Reserver
            bw.Write((ushort)1);   // Type 1 = Ico
            bw.Write((ushort)sizes.Length); // Count

			int offset = 6 + (16 * sizes.Length);

			for ( int i = 0; i < sizes.Length; i++)
			{
				int size = sizes[i];
				byte bSize = (byte)(size >= 256 ? 0 : size);
				int dataLen = (int)pngStreams[i].Length;

				bw.Write(bSize); // Width
				bw.Write(bSize); // Height
				bw.Write((byte)0); // Colors
                bw.Write((byte)0); // Reserved
                bw.Write((ushort)1); // Color planes
                bw.Write((ushort)32); // Bits per pixel
				bw.Write(dataLen);    // Size of Image data
				bw.Write(offset);     // Offset of image data
				offset += dataLen;
            }

			for ( int i = 0; i < sizes.Length; i++)
			{
				bw.Write(pngStreams[i].ToArray());
				pngStreams[i].Dispose();
			}
        }

		private static RenderTargetBitmap RenderIcon(int size)
		{
			var dv = new DrawingVisual();
			using(var dc = dv.RenderOpen())
			{
				double s = size;
				double r = s * 0.20;

				var bgBrush = new LinearGradientBrush(
					Color.FromRgb(0x0f, 0x14, 0x20),
                    Color.FromRgb(0x18, 0x22, 0x38),
					new Point(0,0), new Point(1,1));
				var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0xee, 0x39, 0xd9, 0x8a)), Math.Max(1.0, s * 0.04));
				dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(s * 0.03, s * 0.03, s * 0.94, s * 0.94), r, r);

				var termBg = new SolidColorBrush(Color.FromRgb(0x0a, 0x0d, 0x14));
				var temrPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x26, 0x30, 0x4a)), Math.Max(1.0, s * 0.02));
				dc.DrawRoundedRectangle(termBg, temrPen, new Rect(s * 0.10, s * 0.12, s * 0.80, s * 0.76), s * 0.10, s * 0.10);

				double dotY = s * 0.22;
				double dotR = Math.Max(1.0, s * 0.032);
				dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xff, 0x5c, 0x5c)), null, new Point(s * 0.22, dotY), dotR, dotR);
				dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xff, 0xb0, 0x20)), null, new Point(s * 0.31, dotY), dotR, dotR);
				dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x39, 0xd9, 0x8a)), null, new Point(s * 0.40, dotY), dotR, dotR);

				var senPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0xff, 0xff)), Math.Max(1.0, s * 0.015));
				dc.DrawLine(senPen, new Point(s * 0.10, s * 0.30), new Point(s * 0.09, s * 0.30));

				var chevBrush = new SolidColorBrush(Color.FromRgb(0x39, 0xd9, 0x8a));
				var chevGeo = new StreamGeometry();
				using ( var ctx = chevGeo.Open())
				{
					ctx.BeginFigure(new Point(s * 0.20, s * 0.44), true, true);
					ctx.LineTo(new Point(s * 0.36, s * 0.58), true, false);
					ctx.LineTo(new Point(s * 0.20, s * 0.72), true, false);
					ctx.LineTo(new Point(s * 0.27, s * 0.72), true, false);
					ctx.LineTo(new Point(s * 0.43, s * 0.58), true, false);
					ctx.LineTo(new Point(s * 0.27, s * 0.44), true, false);
				}
				chevGeo.Freeze();
				dc.DrawGeometry(chevBrush, null, chevGeo);

				var cursorBrush = new SolidColorBrush(Color.FromRgb(0x4c, 0x8b, 0xf5));
				dc.DrawRoundedRectangle(cursorBrush, null, new Rect(s * 0.47, s * 0.66, s * 0.15, Math.Max(1.5, s * 0.055)), s * 0.01, s * 0.01);

				var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
				var formatteR = new FormattedText(
					"r",
					System.Globalization.CultureInfo.InvariantCulture,
					FlowDirection.LeftToRight,
					typeface,
					s * 0.32,
					new SolidColorBrush(Color.FromRgb(0x39,0xd9,0x8a)),
					96);
				dc.DrawText(formatteR, new Point(s * 0.66, s * 0.42));
			}
			var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
			rtb.Render(dv);
			return rtb;
		}
}
