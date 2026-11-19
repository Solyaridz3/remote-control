using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteController
{
    // HIMETRIC coordinates on a 120 x 80 mm precision touchpad, not screen
    // contacts or Ctrl+wheel. Windows routes the gesture at the mouse cursor.
    internal interface ITouchpad
    {
        bool Supported { get; }
        void Create();
        void Frame(uint flags, int left, int right);
        void Destroy();
    }

    internal sealed class TouchpadPinch
    {
        private readonly ITouchpad device;
        private bool active;
        private int lastScale = 1000;
        public TouchpadPinch(ITouchpad device) { this.device = device; }
        public bool Supported { get { return device.Supported; } }
        public void Apply(string phase, int scale)
        {
            if (phase == "start")
            {
                Cancel();
                device.Create();
                try { device.Frame(0x4006, 4800, 7200); active = true; lastScale = 1000; }
                catch { device.Destroy(); throw; }
            }
            else if (phase == "update")
            {
                if (!active) throw new InvalidOperationException("No active pinch");
                int half = 1200 * scale / 1000;
                device.Frame(0x4006, 6000 - half, 6000 + half);
                lastScale = scale;
            }
            else if (phase == "end")
            {
                if (!active) return;
                try { int half = 1200 * lastScale / 1000; device.Frame(0x4000, 6000 - half, 6000 + half); }
                finally { Cancel(); }
            }
            else throw new ArgumentException("Invalid pinch phase");
        }
        public void Cancel() { active = false; device.Destroy(); }
    }

    internal sealed class NativeTouchpad : ITouchpad
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
        {
            public uint pointerType, pointerId, frameId, flags;
            public IntPtr sourceDevice, target;
            public Point pixel, himetric, pixelRaw, himetricRaw;
            public uint time, history;
            public int inputData;
            public uint keyStates;
            public ulong performanceCount;
            public uint buttonChange;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct TouchInfo
        {
            public PointerInfo pointer;
            public uint flags, mask;
            public Rect contact, contactRaw;
            public uint orientation, pressure;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PointerTypeInfo { public uint type; public TouchInfo touch; }
        [StructLayout(LayoutKind.Sequential)] internal struct CreationParams
        {
            public uint type, count, feedback;
            public IntPtr monitor;
            public uint width, height, options;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate IntPtr CreateDevice(ref CreationParams parameters);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)] private static extern IntPtr GetOrdinal(IntPtr module, IntPtr ordinal);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] PointerTypeInfo[] contacts, uint count);
        [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(IntPtr device);
        private readonly CreateDevice create;
        private IntPtr handle;

        public NativeTouchpad()
        {
            IntPtr module = GetModuleHandle("user32.dll");
            if (module == IntPtr.Zero) module = LoadLibraryEx("user32.dll", IntPtr.Zero, 0x800);
            if (module == IntPtr.Zero) return;
            IntPtr entry = GetProcAddress(module, "CreateSyntheticPointerDevice2");
            // Microsoft documents ordinal 2690 for earlier Windows 11 exports.
            if (entry == IntPtr.Zero && Environment.OSVersion.Version.Build >= 22000) entry = GetOrdinal(module, new IntPtr(2690));
            if (entry != IntPtr.Zero) create = (CreateDevice)Marshal.GetDelegateForFunctionPointer(entry, typeof(CreateDevice));
        }
        public bool Supported { get { return create != null; } }
        public void Create()
        {
            if (create == null) throw new Win32Exception(50);
            Destroy();
            var parameters = new CreationParams { type = 5, count = 2, feedback = 3, width = 12000, height = 8000, options = 3 };
            handle = create(ref parameters);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Frame(uint flags, int left, int right)
        {
            // Touchpad injection reports physical contact states (including
            // CONFIDENCE), not the DOWN/UPDATE/UP transitions used by touchscreens.
            // See Microsoft's Precision Touchpad Programming Guide injection sample.
            var frame = new[] { Contact(1, flags, left), Contact(2, flags, right) };
            for (int attempt = 0; ; attempt++)
            {
                if (InjectSyntheticPointerInput(handle, frame, 2)) return;
                int error = Marshal.GetLastWin32Error();
                if (error != 21 || attempt >= 3) throw new Win32Exception(error);
                Thread.Sleep(1); // Retry the identical frame if timestamps are too close.
            }
        }
        private static PointerTypeInfo Contact(uint id, uint flags, int x)
        {
            return new PointerTypeInfo { type = 5, touch = new TouchInfo {
                pointer = new PointerInfo { pointerType = 5, pointerId = id, flags = flags,
                    himetric = new Point { x = x, y = 4000 }, himetricRaw = new Point { x = x, y = 4000 } }
            } };
        }
        public void Destroy()
        {
            if (handle == IntPtr.Zero) return;
            IntPtr previous = handle; handle = IntPtr.Zero;
            DestroySyntheticPointerDevice(previous);
        }
    }
}
