using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading;
using RemoteController;

internal static class MagnifierTests
{
    private sealed class FakeMagnifier : IMagnifier
    {
        public volatile bool Enabled;
        public bool Active { get { return Enabled; } }
        public bool Fail;
        public int Radius, Zoom;
        public string Mode;
        public void Configure(bool enabled, int radius, int zoom, string mode) {
            if (Fail) throw new Win32Exception(50);
            Radius = radius; Zoom = zoom; Mode = mode; Enabled = enabled;
        }
        public void Disable() { Enabled = false; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Run()
    {
        int passed = 0;
        Rectangle target, source;
        var bounds = new Rectangle(-1920, -200, 1920, 1080);
        foreach (string mode in new[] { "center", "offset" })
            foreach (var cursor in new[] { new Point(-1920,-200), new Point(-1,879), new Point(-900,400) }) {
                LensGeometry.Calculate(bounds, cursor, 160, 200, mode, out target, out source);
                Check(bounds.Contains(target) && bounds.Contains(source), "Lens escaped monitor bounds");
                Check(target.Width == 320 && source.Width == 157, "Incorrect zoom geometry");
            }
        Console.WriteLine("PASS Circular lens geometry stays on monitors including negative coordinates"); passed++;
        LensGeometry.Calculate(new Rectangle(0,0,1920,1080), new Point(900,600), 160, 200, "offset", out target, out source);
        Check(!target.Contains(900,600) && source.Contains(900,600), "Offset lens obscures cursor");
        Console.WriteLine("PASS Offset lens preserves the click location"); passed++;
        var lens = new FakeMagnifier();
        using (var server = new RemoteServer(new FakeInput(), new PairingStore(null), lens)) {
            server.Start(0);
            using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); Check(peer.Read().Contains("\"magnifier\":true"), "Capability missing");
                peer.Send("{\"type\":\"magnifier\",\"enabled\":true,\"radius\":180,\"zoom\":250,\"mode\":\"offset\"}");
                Check(peer.Read().Contains("magnifier_state") && lens.Enabled && lens.Radius == 180 && lens.Zoom == 250 && lens.Mode == "offset", "Configuration failed");
                server.RotateCode(); Check(!lens.Enabled, "Rotation did not hide lens");
            }
            using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); peer.Read();
                peer.Send("{\"type\":\"magnifier\",\"enabled\":true,\"radius\":80,\"zoom\":150,\"mode\":\"center\"}"); peer.Read();
                Check(lens.Enabled, "Lens not enabled");
            }
            Check(SpinWait.SpinUntil(() => !lens.Enabled, 2000), "Disconnect did not hide lens");
            using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); peer.Read(); lens.Fail = true;
                peer.Send("{\"type\":\"magnifier\",\"enabled\":true,\"radius\":80,\"zoom\":150,\"mode\":\"center\"}");
                Check(peer.Read().Contains("notice") && !lens.Enabled, "Native failure not reported");
                peer.Send("{\"type\":\"ping\"}"); Check(peer.Read().Contains("pong"), "Lens failure broke connection");
            }
        }
        Console.WriteLine("PASS Magnifier configuration, disconnect, revocation and native failure cleanup"); passed++;
        foreach (string fields in new[] { "\"radius\":79,\"zoom\":200,\"mode\":\"center\"", "\"radius\":160,\"zoom\":401,\"mode\":\"center\"", "\"radius\":160,\"zoom\":200,\"mode\":\"other\"" }) {
            using (var server = new RemoteServer(new FakeInput(), new PairingStore(null), new FakeMagnifier())) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read();
                    peer.Send("{\"type\":\"magnifier\",\"enabled\":true," + fields + "}");
                    Check(peer.Closed(), "Invalid lens configuration accepted");
                }
            }
        }
        Console.WriteLine("PASS Invalid lens size, zoom and mode are rejected"); passed++;
        using (var server = new RemoteServer(new FakeInput(), new PairingStore(null), new FakeMagnifier())) {
            server.Start(0); using (var peer = new Peer(server.Port)) {
                peer.Send("{\"type\":\"magnifier\",\"enabled\":true,\"radius\":160,\"zoom\":200,\"mode\":\"center\"}");
                Check(peer.Closed(), "Unauthenticated lens command accepted");
            }
        }
        Console.WriteLine("PASS Magnifier requires an authenticated owner"); passed++;
        lens = new FakeMagnifier();
        using (var server = new RemoteServer(new FakeInput(), new PairingStore(null), lens)) {
            server.Start(0); using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); peer.Read();
                string enable = "{\"type\":\"magnifier\",\"enabled\":true,\"radius\":160,\"zoom\":200,\"mode\":\"center\"}";
                peer.Send(enable); peer.Read(); lens.Disable();
                peer.Send("{\"type\":\"ping\"}");
                Check(peer.Read().Contains("notice") && peer.Read().Contains("pong"), "Runtime lens failure not reported");
                peer.Send(enable); peer.Read(); server.Stop();
                Check(!lens.Enabled, "Stopping server did not hide lens");
            }
        }
        Console.WriteLine("PASS Runtime lens failure is reported and server stop hides lens"); passed++;
        return passed;
    }
}
