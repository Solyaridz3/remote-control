using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RemoteController;

internal sealed class FakeInput : IInput, IPinchInput, ITextInput, IDragInput
{
    public bool PinchSupported { get { return true; } }
    private bool dragging;
    public void SetLeftButton(bool down) { lock (Events) { Events.Add(down ? "drag:start" : "drag:end"); dragging = down; } }
    public void CancelDrag() { lock (Events) { if (dragging) Events.Add("drag:cancel"); dragging = false; } }
    private bool pinching;
    public void Pinch(string phase, int scale) { lock (Events) { Events.Add("pinch:" + phase + ":" + scale); pinching = phase != "end"; } }
    public void CancelGestures() { lock (Events) { if (pinching) Events.Add("pinch:cancel"); pinching = false; } }
    public readonly List<string> Events = new List<string>();
    public void Move(int x, int y) { lock (Events) Events.Add("move:" + x + "," + y); }
    public void Click(bool right) { lock (Events) Events.Add(right ? "right" : "left"); }
    public void Scroll(int delta) { lock (Events) Events.Add("scroll:" + delta); }
    public void Volume(string action) { lock (Events) Events.Add("volume:" + action); }
    public void EditText(int deletions, string text) { lock (Events) Events.Add("text:" + deletions + ":" + text); }
    public int Count { get { lock (Events) return Events.Count; } }
}

internal sealed class Peer : IDisposable
{
    public readonly TcpClient Client = new TcpClient();
    private readonly StreamReader reader;
    private readonly NetworkStream stream;
    public Peer(int port)
    {
        Client.Connect("127.0.0.1", port);
        Client.ReceiveTimeout = 2000;
        stream = Client.GetStream();
        reader = new StreamReader(stream, Encoding.UTF8);
    }
    public void Raw(string text) { var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes, 0, bytes.Length); }
    public void Send(string text) { Raw(text + "\n"); }
    public string Read() { return reader.ReadLine(); }
    public void Hello(string code) { Send("{\"type\":\"hello\",\"version\":1,\"code\":\"" + code + "\"}"); }
    public bool Closed()
    {
        try { string line = Read(); return line == null || (line.Contains("\"error\"") && Read() == null); }
        catch (IOException ex)
        {
            var socket = ex.InnerException as SocketException;
            if (socket != null && socket.SocketErrorCode == SocketError.TimedOut) return false;
            return true;
        }
    }
    public void Dispose() { Client.Close(); }
}

internal static class ProtocolTests
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Wait(Func<bool> condition) { Check(SpinWait.SpinUntil(condition, 2000), "Timed out waiting for condition"); }

    private static int Main()
    {
        try
        {
            Test("Unauthenticated commands cannot reach input", () => {
                var input = new FakeInput();
                using (var server = new RemoteServer(input)) {
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) {
                        peer.Send("{\"type\":\"move\",\"dx\":9,\"dy\":4}");
                        Check(peer.Read().Contains("error"), "Expected rejection");
                        Check(peer.Closed(), "Unauthorized session must close");
                        Check(input.Count == 0, "Unauthorized input dispatched");
                    }
                }
            });
            Test("Fragmented handshake, coalesced commands and heartbeat", () => {
                var input = new FakeInput();
                using (var server = new RemoteServer(input)) {
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) {
                        peer.Raw("{\"type\":\"hel");
                        peer.Send("lo\",\"version\":1,\"code\":\"" + server.Code.ToLowerInvariant() + "\"}");
                        Check(peer.Read().Contains("ready"), "Handshake failed");
                        peer.Raw("{\"type\":\"move\",\"dx\":-12,\"dy\":34}\n{\"type\":\"click\",\"button\":\"left\"}\n{\"type\":\"click\",\"button\":\"right\"}\n{\"type\":\"scroll\",\"delta\":-120}\n{\"type\":\"volume\",\"action\":\"up\"}\n{\"type\":\"volume\",\"action\":\"down\"}\n{\"type\":\"volume\",\"action\":\"mute\"}\n{\"type\":\"ping\"}\n");
                        Check(peer.Read().Contains("pong"), "No heartbeat reply");
                        Check(input.Count == 7, "Missing commands");
                        Check(string.Join("|", input.Events) == "move:-12,34|left|right|scroll:-120|volume:up|volume:down|volume:mute", "Wrong order or values");
                    }
                }
            });
            Test("Only one authenticated owner", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    using (var first = new Peer(server.Port)) using (var second = new Peer(server.Port)) {
                        first.Hello(server.Code); Check(first.Read().Contains("ready"), "First owner rejected");
                        second.Hello(server.Code); Check(second.Read().Contains("error"), "Second owner accepted");
                        first.Send("{\"type\":\"ping\"}"); Check(first.Read().Contains("pong"), "Owner was displaced");
                    }
                }
            });
            Test("Rotating the code revokes the owner and old credentials", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    string old = server.Code;
                    using (var first = new Peer(server.Port)) {
                        first.Hello(old); Check(first.Read().Contains("ready"), "Handshake failed");
                        server.RotateCode(); Check(first.Closed(), "Owner was not disconnected");
                    }
                    using (var next = new Peer(server.Port)) { next.Hello(old); Check(next.Read().Contains("error"), "Old code still valid"); }
                    using (var next = new Peer(server.Port)) { next.Hello(server.Code); Check(next.Read().Contains("ready"), "New code rejected"); }
                }
            });
            string[] invalid = {
                "{\"type\":\"move\",\"dx\":501,\"dy\":0}",
                "{\"type\":\"move\",\"dx\":1.5,\"dy\":0}",
                "{\"type\":\"move\",\"dx\":\"12\",\"dy\":0}",
                "{\"type\":\"move\",\"dx\":true,\"dy\":0}",
                "{\"type\":\"move\",\"dy\":0}",
                "{\"type\":\"scroll\",\"delta\":-1201}",
                "{\"type\":\"volume\",\"action\":\"launch\"}",
                "{\"type\":\"click\",\"button\":\"middle\"}",
                "{\"type\":\"execute\"}", "null", "[]", "broken-json", new string('a', 1025)
            };
            foreach (string frame in invalid) {
                string bad = frame;
                Test("Invalid frame rejected: " + bad.Substring(0, Math.Min(bad.Length, 50)), () => {
                    var input = new FakeInput();
                    using (var server = new RemoteServer(input)) {
                        server.Start(0);
                        using (var peer = new Peer(server.Port)) {
                            peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Handshake failed");
                            peer.Send(bad); Check(peer.Closed(), "Invalid frame did not close session");
                            Check(input.Count == 0, "Invalid input dispatched");
                        }
                    }
                });
            }
            Test("Five incorrect codes trigger cooldown", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    for (int i = 0; i < 5; i++) using (var peer = new Peer(server.Port)) {
                        peer.Hello("WRONG"); Check(peer.Read().Contains("error"), "No auth error");
                    }
                    using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); Check(peer.Read().Contains("error"), "Cooldown did not reject connection");
                    }
                }
            });
            Test("Graceful disconnect releases owner", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Handshake failed");
                        peer.Send("{\"type\":\"disconnect\"}"); Check(peer.Closed(), "Disconnect ignored");
                    }
                    using (var peer = new Peer(server.Port)) { peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Owner not released"); }
                }
            });
            Test("Stop closes sockets and server can restart", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Handshake failed");
                        server.Stop(); Check(peer.Closed(), "Stop left socket alive");
                    }
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) { peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Restart failed"); }
                }
            });
            Test("Idle sessions expire without heartbeat", () => {
                using (var server = new RemoteServer(new FakeInput())) {
                    server.Start(0);
                    using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Handshake failed");
                        peer.Client.ReceiveTimeout = 11000;
                        Check(peer.Closed(), "Idle owner remained connected");
                    }
                }
            });
            passed += WebTests.Run();
            passed += PinchTests.Run();
            passed += PairingTests.Run();
            passed += KeyboardTests.Run();
            passed += MagnifierTests.Run();
            passed += DragTests.Run();
            passed += ScreenTests.Run();
            passed += TlsTests.Run();
            Console.WriteLine("All " + passed + " tests passed. No real input was injected.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
