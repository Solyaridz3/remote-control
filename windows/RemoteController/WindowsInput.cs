using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemoteController
{
    public sealed class WindowsInput : IInput, IPinchInput, ITextInput, IDragInput
    {
        private readonly TouchpadPinch pinch = new TouchpadPinch(new NativeTouchpad());
        private bool leftHeld;
        public void SetLeftButton(bool down) { if (leftHeld == down) return; if (down) leftHeld = true; Send(Mouse(0, 0, down ? 0x0002u : 0x0004u, 0)); leftHeld = down; }
        public void CancelDrag() { if (leftHeld) SetLeftButton(false); }
        public bool PinchSupported { get { return pinch.Supported; } }
        public void Pinch(string phase, int scale) { pinch.Apply(phase, scale); }
        public void CancelGestures() { pinch.Cancel(); }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            public ushort key, scan;
            public uint flags, time;
            public UIntPtr extra;
        }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput mouse;
            [FieldOffset(0)] public KeyboardInput keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint type; public InputUnion data; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        private static Input Mouse(int x, int y, uint flags, uint data)
        {
            return new Input { type = 0, data = new InputUnion { mouse = new MouseInput { dx = x, dy = y, flags = flags, mouseData = data } } };
        }
        private static void Send(params Input[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Move(int x, int y) { Send(Mouse(x, y, 0x0001, 0)); }
        public void EditText(int deletions, string text)
        {
            var inputs = TextInputs(deletions, text);
            if (inputs.Length > 0) Send(inputs);
        }
        internal static Input[] TextInputs(int deletions, string text)
        {
            var inputs = new List<Input>();
            for (int i = 0; i < deletions; i++) AddKey(inputs, 8, 0, 0);
            foreach (char c in text)
            {
                if (c == '\n') AddKey(inputs, 13, 0, 0);
                else if (c == '\t') AddKey(inputs, 9, 0, 0);
                else AddKey(inputs, 0, c, 4);
            }
            return inputs.ToArray();
        }
        private static void AddKey(List<Input> inputs, ushort key, ushort scan, uint flags)
        {
            inputs.Add(new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { key = key, scan = scan, flags = flags } } });
            inputs.Add(new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { key = key, scan = scan, flags = flags | 2 } } });
        }
        public void Click(bool right) { Send(Mouse(0, 0, right ? 0x0008u : 0x0002u, 0), Mouse(0, 0, right ? 0x0010u : 0x0004u, 0)); }
        public void Scroll(int delta) { Send(Mouse(0, 0, 0x0800, unchecked((uint)delta))); }
        public void Volume(string action)
        {
            ushort key = action == "up" ? (ushort)0xAF : action == "down" ? (ushort)0xAE : (ushort)0xAD;
            Send(new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { key = key } } },
                 new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { key = key, flags = 2 } } });
        }
    }
}
