using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteController
{
    internal sealed class DesktopTheme
    {
        private const string PreferencesKey = @"HKEY_CURRENT_USER\Software\RemoteController";
        public readonly bool Dark;
        public readonly Color Canvas, Surface, SurfaceEnd, Feature, FeatureEnd, Field, Ink, Muted;
        public readonly Color Accent, AccentHover, Button, ButtonHover, Border, StatusFill, StatusInk, Error;

        public DesktopTheme(bool dark)
        {
            Dark = dark;
            Canvas = Choose(0x202C43, 0xE9EEFA);
            Surface = Choose(0x33415B, 0xF9FAFF);
            SurfaceEnd = Choose(0x28364F, 0xF0F2FC);
            Feature = Choose(0x414E83, 0xF8FAFF);
            FeatureEnd = Choose(0x303F70, 0xF0F3FC);
            Field = Choose(0x273552, 0xE0E9FA);
            Ink = Choose(0xF5F7FF, 0x182843);
            Muted = Choose(0xC1CDE0, 0x50617C);
            Accent = Choose(0x535ECB, 0x1B57BF);
            AccentHover = Choose(0x626DDE, 0x2868D2);
            Button = Choose(0x43526D, 0xE7EDFB);
            ButtonHover = Choose(0x505F7D, 0xD8E4F8);
            Border = Choose(0x576783, 0xC8D5EB);
            StatusFill = Choose(0x214D49, 0xD7F0E5);
            StatusInk = Choose(0xABF3D5, 0x165C45);
            Error = Choose(0xFFD1A8, 0x8F351D);
        }

        private Color Choose(int dark, int light) { return Color.FromArgb(unchecked((int)0xFF000000) | (Dark ? dark : light)); }

        public static bool LoadDark()
        {
            try { return !string.Equals(Registry.GetValue(PreferencesKey, "Theme", "dark") as string, "light", StringComparison.Ordinal); }
            catch (Exception ex) { if (!PreferenceUnavailable(ex)) throw; return true; }
        }

        public void Save()
        {
            try { Registry.SetValue(PreferencesKey, "Theme", Dark ? "dark" : "light"); }
            catch (Exception ex) { if (!PreferenceUnavailable(ex)) throw; }
        }

        private static bool PreferenceUnavailable(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException;
        }

        public static void Round(Control control, int radius)
        {
            EventHandler update = (sender, e) =>
            {
                if (control.Width < 2 || control.Height < 2) return;
                using (var path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), radius))
                {
                    var previous = control.Region;
                    control.Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            };
            control.SizeChanged += update;
            update(control, EventArgs.Empty);
        }

        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ThemeCard : TableLayoutPanel
    {
        private Color bottom;
        private Color border;

        public ThemeCard(Padding padding)
        {
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 1;
            Padding = padding;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DesktopTheme.Round(this, 20);
        }

        public void Apply(DesktopTheme theme, bool featured = false)
        {
            bottom = featured ? theme.FeatureEnd : theme.SurfaceEnd;
            border = theme.Border;
            BackColor = featured ? theme.Feature : theme.Surface;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2 || bottom.IsEmpty) { base.OnPaintBackground(e); return; }
            using (var fill = new LinearGradientBrush(ClientRectangle, BackColor, bottom, LinearGradientMode.ForwardDiagonal))
                e.Graphics.FillRectangle(fill, ClientRectangle);
            using (var line = new Pen(border))
            using (var outline = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 20))
            {
                var smoothing = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(line, outline);
                e.Graphics.SmoothingMode = smoothing;
            }
        }
    }
}
