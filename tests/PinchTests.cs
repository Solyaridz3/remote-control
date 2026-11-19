using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteController;

internal static class PinchTests
{
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private sealed class Device : ITouchpad
    {
        public bool Supported { get { return true; } }
        public bool Created, Fail;
        public readonly List<string> Frames = new List<string>();
        public void Create() { Created = true; }
        public void Frame(uint flags, int left, int right)
        {
            if (Fail) throw new Win32Exception(87);
            Frames.Add(flags + ":" + left + ":" + right);
        }
        public void Destroy() { Created = false; }
    }
    private sealed class FailingInput : IInput, IPinchInput
    {
        public bool PinchSupported { get { return true; } }
        public void Move(int x, int y) { }
        public void Click(bool right) { }
        public void Scroll(int delta) { }
        public void Volume(string action) { }
        public void Pinch(string phase, int scale) { throw new Win32Exception(87); }
        public void CancelGestures() { }
    }
    public static int Run()
    {
        Test("Native pointer ABI layout matches Win64 headers", () => {
            Check(Marshal.SizeOf(typeof(NativeTouchpad.PointerInfo)) == 96, "POINTER_INFO size");
            Check(Marshal.SizeOf(typeof(NativeTouchpad.TouchInfo)) == 144, "POINTER_TOUCH_INFO size");
            Check(Marshal.SizeOf(typeof(NativeTouchpad.PointerTypeInfo)) == 152, "POINTER_TYPE_INFO size");
            Check(Marshal.OffsetOf(typeof(NativeTouchpad.PointerTypeInfo), "touch").ToInt32() == 8, "Union offset");
            Check(Marshal.SizeOf(typeof(NativeTouchpad.CreationParams)) == 40, "Creation params size");
        });
        Test("Pinch injects contact down, physical spread and up at last positions", () => {
            var device = new Device(); var pinch = new TouchpadPinch(device);
            pinch.Apply("start", 1000); pinch.Apply("update", 1500); pinch.Apply("end", 1000);
            Check(string.Join("|", device.Frames) == "16390:4800:7200|16390:4200:7800|16384:4200:7800", "Wrong native contact sequence");
            Check(!device.Created, "Device leaked");
        });
        Test("Pinch cancellation and failure destroy contacts", () => {
            var device = new Device(); var pinch = new TouchpadPinch(device);
            pinch.Apply("start", 1000); pinch.Cancel(); pinch.Cancel(); Check(!device.Created, "Cancel leaked device");
            device.Fail = true;
            try { pinch.Apply("start", 1000); throw new Exception("Expected native failure"); } catch (Win32Exception) { }
            Check(!device.Created, "Failed begin leaked device");
        });
        Test("Native precision touchpad API availability and device creation (no input)", () => {
            var device = new NativeTouchpad();
            Console.WriteLine("Native precision touchpad API available: " + device.Supported);
            if (device.Supported) { try { device.Create(); } finally { device.Destroy(); } }
        });
        Test("Paired protocol carries pinch phases and scaling", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); Check(peer.Read().Contains("\"pinch\":true"), "No capability");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"update\",\"scale\":1250}");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"end\"}");
                    peer.Send("{\"type\":\"ping\"}"); Check(peer.Read().Contains("pong"), "Session closed");
                    Check(string.Join("|", input.Events) == "pinch:start:1000|pinch:update:1250|pinch:end:1000", "Wrong pinch commands");
                }
            }
        });
        Test("Rotating code cancels active contacts before revoking owner", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}");
                    peer.Send("{\"type\":\"ping\"}"); peer.Read(); server.RotateCode();
                    Check(peer.Closed(), "Owner not revoked");
                    Check(string.Join("|", input.Events) == "pinch:start:1000|pinch:cancel", "Active contacts not cancelled");
                }
            }
        });
        Test("Socket close cancels active contacts", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}");
                    peer.Send("{\"type\":\"disconnect\"}"); Check(peer.Closed(), "Socket did not close");
                    Check(string.Join("|", input.Events) == "pinch:start:1000|pinch:cancel", "Disconnect leaked contacts");
                }
            }
        });
        string[] badFrames = {
            "{\"type\":\"pinch\",\"phase\":\"update\",\"scale\":1500}",
            "{\"type\":\"pinch\",\"phase\":\"end\"}",
            "{\"type\":\"pinch\",\"phase\":\"bad\"}"
        };
        foreach (string frame in badFrames) {
            string bad = frame;
            Test("Reject invalid pinch lifecycle: " + bad, () => {
                var input = new FakeInput();
                using (var server = new RemoteServer(input)) {
                    server.Start(0); using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); peer.Read(); peer.Send(bad); Check(peer.Closed(), "Invalid lifecycle accepted"); Check(input.Count == 0, "Invalid input dispatched");
                    }
                }
            });
        }
        Test("Invalid pinch scale closes socket and cancels contacts", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"update\",\"scale\":4001}");
                    Check(peer.Closed(), "Invalid scale accepted"); Check(input.Events.Contains("pinch:cancel"), "Failed command leaked contacts");
                }
            }
        });
        Test("Native failure reports a notice and drains pending gesture without disconnecting", () => {
            using (var server = new RemoteServer(new FailingInput())) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}");
                    Check(peer.Read().Contains("notice"), "Missing failure message");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"update\",\"scale\":1500}");
                    peer.Send("{\"type\":\"pinch\",\"phase\":\"end\"}");
                    peer.Send("{\"type\":\"ping\"}"); Check(peer.Read().Contains("pong"), "Failure killed mouse controls");
                }
            }
        });
        return passed;
    }
}
