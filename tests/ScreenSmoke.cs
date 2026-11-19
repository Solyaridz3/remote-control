using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteController;

internal static class ScreenSmoke
{
    private sealed class NoInput : IInput {
        public void Move(int x,int y) {} public void Click(bool right) {} public void Scroll(int delta) {} public void Volume(string action) {}
    }
    private sealed class Channel : IDisposable {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        public Channel(int port) { socket.Options.SetRequestHeader("Origin","http://127.0.0.1:"+port);using(var timeout=new CancellationTokenSource(3000))socket.ConnectAsync(new Uri("ws://127.0.0.1:"+port+"/ws"),timeout.Token).GetAwaiter().GetResult(); }
        public void Send(string text) { using(var timeout=new CancellationTokenSource(3000))socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),WebSocketMessageType.Text,true,timeout.Token).GetAwaiter().GetResult(); }
        public byte[] Read() { using(var data=new MemoryStream())using(var timeout=new CancellationTokenSource(3000)){var buffer=new byte[65536];while(true){var result=socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token).GetAwaiter().GetResult();data.Write(buffer,0,result.Count);if(result.EndOfMessage)return data.ToArray();}} }
        public void Dispose() { socket.Abort();socket.Dispose(); }
    }
    private static void Main()
    {
        // Measure the real desktop capture without saving or printing any pixels.
        using (var capture = new DesktopScreenSource()) {
            var clock = Stopwatch.StartNew(); long bytes = 0;
            for (int i=0;i<60;i++) {
                byte[] frame=capture.Capture(390,681,80);bytes+=frame.Length;
                if(i==0)using(var data=new MemoryStream(frame))using(var image=Image.FromStream(data))
                    if(image.Width!=390 || image.Height!=681)throw new Exception("Incorrect frame dimensions");
            }
            Console.WriteLine("Native JPEG capture: 60 valid frames, average {0:F1} ms/frame, {1:F0} KB/frame. No images saved.",clock.Elapsed.TotalMilliseconds/60,bytes/60.0/1024);
        }
        using(var server=new RemoteServer(new NoInput(),new PairingStore(null),null,()=>new DesktopScreenSource())) {
            server.Start(0);using(var owner=new Channel(server.Port)) {
                owner.Send("{\"type\":\"hello\",\"version\":1,\"code\":\""+server.Code+"\"}");owner.Read();
                owner.Send("{\"type\":\"screen_start\",\"id\":1}");
                var reply=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(Encoding.UTF8.GetString(owner.Read()));
                using(var viewer=new Channel(server.Port)) {
                    viewer.Send("{\"type\":\"screen_hello\",\"ticket\":\""+(string)reply["ticket"]+"\"}");viewer.Read();
                    var clock=Stopwatch.StartNew();
                    for(int seq=1;seq<=120;seq++) {viewer.Send("{\"type\":\"screen_next\",\"seq\":"+seq+",\"width\":390,\"height\":681,\"quality\":80,\"fps\":60}");viewer.Read();}
                    Console.WriteLine("Local authenticated capture + transport: {0:F1} frames/sec (120 frames). No images saved.",120/clock.Elapsed.TotalSeconds);
                }
            }
        }
    }
}
