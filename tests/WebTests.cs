using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using RemoteController;

internal static class WebTests
{
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static string Url(RemoteServer server) { return "http://127.0.0.1:" + server.Port; }
    private static string Hello(RemoteServer server) { return "{\"type\":\"hello\",\"version\":1,\"code\":\"" + server.Code + "\"}"; }

    internal sealed class Browser : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        public Browser(RemoteServer server)
        {
            socket.Options.SetRequestHeader("Origin", Url(server));
            using (var timeout = new CancellationTokenSource(3000))
                socket.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port + "/ws"), timeout.Token).GetAwaiter().GetResult();
        }
        public void Send(string text, bool final)
        {
            using (var timeout = new CancellationTokenSource(3000))
                socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, final, timeout.Token).GetAwaiter().GetResult();
        }
        public void Send(string text) { Send(text, true); }
        public string Read()
        {
            byte[] buffer = new byte[2048];
            using (var all = new MemoryStream()) using (var timeout = new CancellationTokenSource(3000))
            {
                while (true)
                {
                    var result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    all.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return Encoding.UTF8.GetString(all.ToArray());
                }
            }
        }
        public bool Closed() { try { string line = Read(); return line == null || (line.Contains("\"error\"") && Read() == null); } catch (WebSocketException) { return true; } }
        public byte[] ReadBinary()
        {
            byte[] buffer = new byte[16384];
            using (var data = new MemoryStream()) using (var timeout = new CancellationTokenSource(3000)) {
                while (true) {
                    var result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                    if (result.MessageType != WebSocketMessageType.Binary) throw new Exception("Expected a binary frame");
                    data.Write(buffer, 0, result.Count);
                    if (data.Length > 2097152) throw new Exception("Frame too large");
                    if (result.EndOfMessage) return data.ToArray();
                }
            }
        }
        public void Dispose() { socket.Abort(); socket.Dispose(); }
    }

    private static string Fetch(RemoteServer server, string path)
    {
        var request = (HttpWebRequest)WebRequest.Create(Url(server) + path);
        request.Proxy = null; request.KeepAlive = false; request.Timeout = 3000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream()))
        {
            Check(response.StatusCode == HttpStatusCode.OK, "HTTP failed");
            Check(response.Headers["Cache-Control"] == "no-store", "Assets could become stale");
            Check(response.Headers["X-Frame-Options"] == "DENY", "No frame protection");
            return reader.ReadToEnd();
        }
    }
    public static int Run()
    {
        Test("Embedded page and all local assets served without authentication", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                Check(Fetch(server, "/").Contains("id=\"touchpad\""), "Missing pad");
                Check(Fetch(server, "/app.css").Contains("touch-action:none"), "Missing gesture CSS");
                Check(Fetch(server, "/app.js").Contains("new WebSocket"), "Missing controller");
                Check(Fetch(server, "/viewport.js").Contains("appHeight"), "Missing viewport helper");
                Check(Fetch(server, "/keyboard.js").Contains("nextEdit"), "Missing keyboard helper");
                Check(Fetch(server, "/gestures.js").Contains("GestureTracker"), "Missing gestures");
                Check(Fetch(server, "/manifest.webmanifest").Contains("standalone"), "Missing manifest");
                Check(Fetch(server, "/icon.svg").Contains("<svg"), "Missing icon");
            }
        });
        Test("Unlisted HTTP paths return 404 and cannot read source files", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                using (var peer = new Peer(server.Port)) {
                    peer.Raw("GET /windows/RemoteController/Program.cs HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n");
                    Check(peer.Read().Contains("404"), "Source file exposed");
                }
            }
        });
        Test("HTTP rejects foreign Host headers (DNS rebinding)", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                using (var peer = new Peer(server.Port)) {
                    peer.Raw("GET / HTTP/1.1\r\nHost: attacker.example:" + server.Port + "\r\n\r\n");
                    Check(peer.Read().Contains("403"), "Foreign Host accepted");
                }
            }
        });
        Test("WebSocket rejects a foreign or missing Origin", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                foreach (string origin in new[] { "Origin: http://attacker.example\r\n", "" })
                using (var peer = new Peer(server.Port)) {
                    peer.Raw("GET /ws HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n" + origin + "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
                    Check(peer.Read().Contains("403"), "Foreign Origin accepted");
                }
            }
        });
        Test("Standards client upgrades, fragmented hello authenticates and commands work", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var browser = new Browser(server)) {
                    string hello = Hello(server);
                    browser.Send(hello.Substring(0, 15), false); browser.Send(hello.Substring(15));
                    Check(browser.Read().Contains("ready"), "Browser handshake failed");
                    browser.Send("{\"type\":\"move\",\"dx\":10,\"dy\":-8}");
                    browser.Send("{\"type\":\"click\",\"button\":\"right\"}");
                    browser.Send("{\"type\":\"volume\",\"action\":\"down\"}");
                    browser.Send("{\"type\":\"scroll\",\"delta\":120}");
                    browser.Send("{\"type\":\"ping\"}");
                    Check(browser.Read().Contains("pong"), "Browser heartbeat failed");
                    Check(string.Join("|", input.Events) == "move:10,-8|right|volume:down|scroll:120", "Wrong browser commands");
                }
            }
        });
        Test("Browser cannot control input before pairing", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var browser = new Browser(server)) {
                    browser.Send("{\"type\":\"volume\",\"action\":\"up\"}");
                    Check(browser.Read().Contains("error"), "Unpaired browser accepted");
                    Check(input.Count == 0, "Unpaired command dispatched");
                }
            }
        });
        Test("Native and browser clients share one owner", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                using (var browser = new Browser(server)) using (var native = new Peer(server.Port)) {
                    browser.Send(Hello(server)); Check(browser.Read().Contains("ready"), "Browser failed");
                    native.Hello(server.Code); Check(native.Read().Contains("error"), "Native displaced browser");
                    browser.Send("{\"type\":\"ping\"}"); Check(browser.Read().Contains("pong"), "Browser owner lost");
                }
            }
        });
        Test("Code rotation disconnects paired browser", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0);
                using (var browser = new Browser(server)) {
                    browser.Send(Hello(server)); Check(browser.Read().Contains("ready"), "Browser failed");
                    server.RotateCode(); Check(browser.Closed(), "Browser not revoked");
                }
            }
        });
        Test("Oversized WebSocket payload cannot reach input", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var browser = new Browser(server)) {
                    browser.Send(Hello(server)); Check(browser.Read().Contains("ready"), "Browser failed");
                    browser.Send(new string('x', 1025)); Check(browser.Closed(), "Large payload accepted");
                    Check(input.Count == 0, "Invalid input dispatched");
                }
            }
        });
        Test("Multiple JSON commands in one WebSocket message are rejected", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0);
                using (var browser = new Browser(server)) {
                    browser.Send(Hello(server)); Check(browser.Read().Contains("ready"), "Browser failed");
                    browser.Send("{\"type\":\"ping\"}\n{\"type\":\"volume\",\"action\":\"up\"}");
                    Check(browser.Closed(), "Multiple commands accepted"); Check(input.Count == 0, "Invalid input dispatched");
                }
            }
        });
        return passed;
    }
}
