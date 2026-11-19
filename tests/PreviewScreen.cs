using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using RemoteController;

internal sealed class PreviewScreen : IScreenSource
{
    private int frame;
    public byte[] Capture(int width, int height, int quality)
    {
        using (var bitmap = new Bitmap(width, height)) using (var g = Graphics.FromImage(bitmap)) {
            g.Clear(Color.FromArgb(23,34,49));
            using (var pen = new Pen(Color.FromArgb(45,67,86)))
                for (int i = 0; i < Math.Max(width,height); i += 40) { g.DrawLine(pen, i, 0, i, height); g.DrawLine(pen, 0, i, width, i); }
            using (var font = new Font("Segoe UI", 22, FontStyle.Bold)) g.DrawString("REMOTE\nТрансляция экрана", font, Brushes.White, 22, 72);
            using (var font = new Font("Segoe UI", 13)) g.DrawString("Жесты работают поверх изображения\nТестовый кадр " + (++frame), font, Brushes.LightGray, 22, 170);
            int x = (int)(width / 2 + Math.Sin(frame * .035) * width * .25);
            int y = height / 2;
            using (var brush = new SolidBrush(Color.FromArgb(106,227,185))) g.FillEllipse(brush, x - 18, y - 18, 36, 36);
            using (var data = new MemoryStream()) { bitmap.Save(data, ImageFormat.Jpeg); return data.ToArray(); }
        }
    }
    public void Dispose() { }
}
