using System;
using System.Drawing;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteController;

internal static class ScreenTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private sealed class Source : IScreenSource {
        public int Calls;
        public volatile bool Disposed;
        public readonly ManualResetEvent Entered = new ManualResetEvent(false), Continue = new ManualResetEvent(true);
        public byte[] Capture(int w,int h,int q) { Interlocked.Increment(ref Calls); Entered.Set(); Continue.WaitOne(); return new byte[70000]; }
        public void Dispose() { Disposed = true; }
    }
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    private static string Next(int seq) { return Json.Serialize(new {type="screen_next",seq=seq,width=390,height=681,quality=80,fps=60}); }
    private static WebTests.Browser Owner(RemoteServer server) {
        var peer = new WebTests.Browser(server); peer.Send(Json.Serialize(new {type="hello",version=1,code=server.Code}));
        Check(peer.Read().Contains("\"screen\":true"),"Screen capability missing"); return peer;
    }
    private static string Ticket(WebTests.Browser owner,int id) {
        owner.Send(Json.Serialize(new {type="screen_start",id=id}));
        var reply = Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(owner.Read());
        Check((string)reply["type"] == "screen_ready" && (int)reply["id"] == id,"Wrong screen response");
        return (string)reply["ticket"];
    }
    private static WebTests.Browser Viewer(RemoteServer server,string ticket) {
        var peer = new WebTests.Browser(server); peer.Send(Json.Serialize(new {type="screen_hello",ticket=ticket}));
        Check(peer.Read().Contains("screen_connected"),"Screen handshake failed");return peer;
    }
    public static int Run()
    {
        int passed = 0;
        var follower = new CropFollower(); var monitor = new Rectangle(-1920,0,1920,1080);
        var first = follower.Update(monitor,new Point(-900,500),new Size(390,681),16);
        var stable = follower.Update(monitor,new Point(-890,510),new Size(390,681),16);
        Check(first == stable,"Crop moved within dead zone");
        var shifted = follower.Update(monitor,new Point(-740,500),new Size(390,681),16);
        Check(shifted.X > first.X && shifted.X < first.X + 70,"Crop did not follow smoothly");
        var edge = follower.Update(monitor,new Point(-1,1079),new Size(390,681),16);
        Check(monitor.Contains(edge) && edge.Contains(-1,1079),"Crop escaped monitor or lost cursor");
        var small = follower.Update(new Rectangle(0,0,800,600),new Point(400,300),new Size(960,1280),16);
        Check(new Rectangle(0,0,800,600).Contains(small) && Math.Abs(small.Width/(double)small.Height - .75) < .01,"Crop aspect ratio changed");
        Console.WriteLine("PASS Screen crop dead zone, smooth follow, aspect ratio and monitor edges");passed++;
        var source = new Source();var input = new FakeInput();
        using(var server = new RemoteServer(input,new PairingStore(null),null,()=>source)) {
            server.Start(0);
            using(var bad = new WebTests.Browser(server)) {bad.Send(Json.Serialize(new {type="screen_hello",ticket=new string('A',64)}));Check(bad.Read().Contains("screen_denied") && source.Calls==0,"Unpaired screen capture allowed");}
            using(var owner = Owner(server)) {
                string ticket = Ticket(owner,1);
                using(var viewer=Viewer(server,ticket)) {
                    viewer.Send(Next(1));Check(viewer.ReadBinary().Length==70000,"Large binary frame truncated");
                    owner.Send("{\"type\":\"move\",\"dx\":10,\"dy\":5}");owner.Send("{\"type\":\"ping\"}");
                    Check(owner.Read().Contains("pong") && input.Count==1,"Streaming blocked pointer input");
                    owner.Send("{\"type\":\"screen_stop\"}");Check(owner.Read().Contains("screen_stopped"),"Stop not acknowledged");
                }
                Check(SpinWait.SpinUntil(()=>source.Disposed,2000),"Capture not disposed after stop");
                using(var old=new WebTests.Browser(server)){old.Send(Json.Serialize(new {type="screen_hello",ticket=ticket}));Check(old.Read().Contains("screen_denied"),"Stopped ticket still works");}
            }
        }
        Console.WriteLine("PASS Screen authentication, large binary frames, input isolation and stop revocation");passed++;
        source = new Source();source.Continue.Reset();
        using(var server = new RemoteServer(new FakeInput(),new PairingStore(null),null,()=>source)) {
            server.Start(0);using(var owner=Owner(server))using(var viewer=Viewer(server,Ticket(owner,1))) {
                viewer.Send(Next(1));Check(source.Entered.WaitOne(2000),"Capture not started");
                try {
                    owner.Send("{\"type\":\"ping\"}");Check(owner.Read().Contains("pong"),"Slow capture held input gate");
                    server.RotateCode();
                } finally { source.Continue.Set(); }
                Check(SpinWait.SpinUntil(()=>source.Disposed,2000),"Revocation left capture alive");
            }
        }
        Console.WriteLine("PASS Slow screen capture cannot block input or survive revocation");passed++;
        source = new Source();
        using(var server = new RemoteServer(new FakeInput(),new PairingStore(null),null,()=>source)) {
            server.Start(0);using(var owner=Owner(server))using(var viewer=Viewer(server,Ticket(owner,1))) {
                viewer.Send(Next(2));Check(viewer.Closed() && source.Calls==0,"Out of order frame request accepted");
                owner.Send("{\"type\":\"ping\"}");Check(owner.Read().Contains("pong"),"Invalid stream broke control connection");
            }
        }
        Console.WriteLine("PASS Invalid screen request only closes the screen channel");passed++;
        return passed;
    }
}
