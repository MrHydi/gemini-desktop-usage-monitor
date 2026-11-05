using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using GeminiUsageMonitor.Models;

namespace GeminiUsageMonitor.Services;

internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    public static Icon CreateStatusIcon(HighlightLevel level, int? currentPercent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            Color bgColor = level switch
            {
                HighlightLevel.Red90 => Color.FromArgb(235, 229, 57, 53),
                HighlightLevel.Amber75 => Color.FromArgb(240, 245, 158, 11),
                _ => Color.FromArgb(235, 37, 99, 235)
            };

            using var bgBrush = new SolidBrush(bgColor);
            using var path = CreateRoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(bgBrush, path);

            string label = currentPercent.HasValue
                ? (currentPercent.Value >= 100 ? "99" : currentPercent.Value.ToString())
                : "G";

            float fontSize = label.Length >= 2 ? 12.5f : 15f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(label, font, textBrush, new RectangleF(0, 1, 32, 31), sf);
        }

        nint hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
