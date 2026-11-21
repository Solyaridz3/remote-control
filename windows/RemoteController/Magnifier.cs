using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteController
{
    // A separate message loop keeps network cleanup independent of the main window.
    public sealed class MagnifierService : IMagnifier, IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent started = new ManualResetEvent(false);
        private LensWindow window;
        private Exception startupError;
        public MagnifierService() : this(false) { }
        internal MagnifierService(bool inspectWindow)
        {
            thread = new Thread(() => {
                try {
                    window = new LensWindow(inspectWindow);
                    IntPtr handle = window.Handle;
                    started.Set();
                    Application.Run();
                } catch (Exception ex) { startupError = ex; started.Set(); }
                finally { if (window != null) window.Dispose(); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); started.WaitOne();
        }
        public void Configure(bool enabled, int radius, int zoom, string mode)
        {
            if (startupError != null || !thread.IsAlive) throw new Win32Exception(50);
            Exception failure = null;
            window.Invoke((Action)(() => { try { window.Configure(enabled, radius, zoom, mode); } catch (Win32Exception ex) { failure = ex; } }));
            if (failure != null) throw failure;
        }
        public void Disable()
        {
            if (startupError == null && thread.IsAlive)
                window.Invoke((Action)(() => window.Disable()));
        }
        public bool Active { get { return startupError == null && thread.IsAlive && (bool)window.Invoke(new Func<bool>(() => window.Active)); } }
        public void Dispose()
        {
            if (startupError == null && thread.IsAlive) {
                window.Invoke((Action)(() => { window.Disable(); Application.ExitThread(); }));
                thread.Join();
            }
            started.Dispose();
        }
    }

    internal sealed class LensWindow : Form
    {
        private IntPtr child;
        private bool initialized;
        private int radius = 160, zoom = 200;
        private string mode = "center";
        private readonly System.Windows.Forms.Timer timer;
        private readonly bool inspectWindow;
        public bool Active { get { return timer.Enabled; } }
        public LensWindow(bool inspectWindow)
        {
            this.inspectWindow = inspectWindow;
            Text = UiText.Get("PocketPointer · Лупа");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = inspectWindow;
            BackColor = Color.FromArgb(106, 227, 185);
            AutoScaleMode = AutoScaleMode.None;
            timer = new System.Windows.Forms.Timer { Interval = 16 };
            timer.Tick += (s, e) => { try { UpdateLens(); } catch (Win32Exception) { Disable(); } };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 8; if (!inspectWindow) p.ExStyle |= 0x80; return p; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
            if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
            base.WndProc(ref message);
        }
        public void Configure(bool enabled, int newRadius, int newZoom, string newMode)
        {
            if (!enabled) { Disable(); return; }
            if (newRadius < 80 || newRadius > 280 || newZoom < 150 || newZoom > 400 || (newMode != "center" && newMode != "offset")) throw new ArgumentOutOfRangeException();
            try {
                if (!initialized) {
                    Require(Native.MagInitialize()); initialized = true;
                    Require(Native.SetLayeredWindowAttributes(Handle, 0, 255, 2));
                    child = Native.CreateWindowEx(0, "Magnifier", null, 0x50000001, 3, 3, 100, 100, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (child == IntPtr.Zero) throw new Win32Exception();
                    Require(Native.MagSetWindowFilterList(child, 0, 1, new[] { Handle }));
                }
                radius = newRadius; zoom = newZoom; mode = newMode;
                int diameter = radius * 2;
                Size = new Size(diameter, diameter);
                using (var path = new GraphicsPath()) {
                    path.AddEllipse(0, 0, diameter, diameter);
                    var oldRegion = Region; Region = new Region(path);
                    if (oldRegion != null) oldRegion.Dispose();
                }
                Require(Native.SetWindowPos(child, IntPtr.Zero, 3, 3, diameter - 6, diameter - 6, 0x14));
                IntPtr clip = Native.CreateEllipticRgn(0, 0, diameter - 6, diameter - 6);
                if (clip == IntPtr.Zero) throw new Win32Exception();
                if (Native.SetWindowRgn(child, clip, true) == 0) { Native.DeleteObject(clip); throw new Win32Exception(); }
                UpdateLens();
                Show(); timer.Start();
            } catch { Disable(); throw; }
        }
        private static void Require(bool success) { if (!success) throw new Win32Exception(); }
        private void UpdateLens()
        {
            Native.Point cursor;
            Require(Native.GetCursorPos(out cursor));
            var bounds = Screen.FromPoint(new Point(cursor.X, cursor.Y)).Bounds;
            Rectangle target, source;
            LensGeometry.Calculate(bounds, new Point(cursor.X, cursor.Y), radius, zoom, mode, out target, out source);
            Require(Native.SetWindowPos(Handle, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, 0x10));
            float factor = (target.Width - 6) / (float)source.Width;
            var transform = new Native.Transform { a = factor, e = factor, i = 1 };
            Require(Native.MagSetWindowTransform(child, ref transform));
            Require(Native.MagSetWindowSource(child, new Native.Rect { left = source.Left, top = source.Top, right = source.Right, bottom = source.Bottom }));
            Native.InvalidateRect(child, IntPtr.Zero, true);
        }
        public void Disable() { timer.Stop(); Hide(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            if (child != IntPtr.Zero) { Native.DestroyWindow(child); child = IntPtr.Zero; }
            if (initialized) { Native.MagUninitialize(); initialized = false; }
            base.Dispose(disposing);
        }
        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int left, top, right, bottom; }
            [StructLayout(LayoutKind.Sequential)] internal struct Transform { public float a,b,c,d,e,f,g,h,i; }
            [DllImport("Magnification.dll", SetLastError=true)] internal static extern bool MagInitialize();
            [DllImport("Magnification.dll")] internal static extern bool MagUninitialize();
            [DllImport("Magnification.dll", SetLastError=true)] internal static extern bool MagSetWindowSource(IntPtr hwnd, Rect rect);
            [DllImport("Magnification.dll", SetLastError=true)] internal static extern bool MagSetWindowTransform(IntPtr hwnd, ref Transform transform);
            [DllImport("Magnification.dll", SetLastError=true)] internal static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] windows);
            [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
            [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, int flags);
            [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
            [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetCursorPos(out Point point);
            [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
            [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError=true)] internal static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
            [DllImport("gdi32.dll")] internal static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
            [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
        }
    }
    internal static class LensGeometry
    {
        internal static void Calculate(Rectangle bounds, Point cursor, int radius, int zoom, string mode, out Rectangle target, out Rectangle source)
        {
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            int x = cursor.X - diameter / 2, y = cursor.Y - diameter / 2;
            if (mode == "offset") {
                x = cursor.X + 24; y = cursor.Y - diameter - 24;
                if (x + diameter > bounds.Right) x = cursor.X - diameter - 24;
                if (y < bounds.Top) y = cursor.Y + 24;
            }
            target = new Rectangle(Clamp(x, bounds.Left, bounds.Right - diameter), Clamp(y, bounds.Top, bounds.Bottom - diameter), diameter, diameter);
            int span = Math.Max(1, (int)Math.Round((diameter - 6) * 100.0 / zoom));
            source = new Rectangle(Clamp(cursor.X - span / 2, bounds.Left, bounds.Right - span), Clamp(cursor.Y - span / 2, bounds.Top, bounds.Bottom - span), span, span);
        }
        private static int Clamp(int value, int low, int high) { return Math.Max(low, Math.Min(high, value)); }
    }
}
