using System;
using System.Threading;
using RemoteController;

internal static class DragTests
{
    private const string Start = "{\"type\":\"drag\",\"phase\":\"start\"}";
    private const string End = "{\"type\":\"drag\",\"phase\":\"end\"}";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Barrier(Peer peer) { peer.Send("{\"type\":\"ping\"}"); Check(peer.Read().Contains("pong"), "No response"); }
    private static string Events(FakeInput input) { lock(input.Events) return string.Join("|",input.Events); }
    public static int Run()
    {
        int passed = 0;
        var input = new FakeInput();
        using (var server = new RemoteServer(input)) {
            server.Start(0); using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); Check(peer.Read().Contains("\"drag\":true"), "Drag capability missing");
                peer.Send(Start); peer.Send("{\"type\":\"move\",\"dx\":25,\"dy\":10}"); peer.Send(End); Barrier(peer);
                Check(Events(input) == "drag:start|move:25,10|drag:end", "Incorrect drag sequence");
            }
        }
        Console.WriteLine("PASS Drag holds the left button around relative movement"); passed++;
        foreach (string cleanup in new[] { "close", "rotate", "stop", "invalid" }) {
            input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); peer.Send(Start); Barrier(peer);
                    if (cleanup == "rotate") server.RotateCode();
                    else if (cleanup == "stop") server.Stop();
                    else if (cleanup == "invalid") peer.Send(Start);
                    else peer.Dispose();
                    Check(SpinWait.SpinUntil(() => Events(input).Contains("drag:cancel"), 2000), "Drag held after " + cleanup);
                }
            }
            Check(Events(input) == "drag:start|drag:cancel", "Duplicate drag release after " + cleanup);
            Console.WriteLine("PASS Drag releases exactly once on " + cleanup); passed++;
        }
        input = new FakeInput();
        using (var server = new RemoteServer(input)) {
            server.Start(0); using (var peer = new Peer(server.Port)) {
                peer.Send(Start); Check(peer.Closed() && input.Count == 0, "Unpaired drag reached Windows");
            }
            using (var peer = new Peer(server.Port)) {
                peer.Hello(server.Code); peer.Read(); peer.Send(End);
                Check(peer.Closed() && input.Count == 0, "Orphan drag release accepted");
            }
        }
        Console.WriteLine("PASS Drag requires authentication and a matching start"); passed++;
        return passed;
    }
}
