using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteController
{
    public sealed class SetupResponse
    {
        public readonly string ContentType;
        public readonly byte[] Body;
        public SetupResponse(string contentType, byte[] body) { ContentType = contentType; Body = body; }
    }
    public sealed class TransportSecurity
    {
        public readonly Func<X509Certificate2> Certificate;
        public readonly Func<string, string, SetupResponse> Setup;
        public readonly Func<TcpClient, bool> AllowConnection;
        public TransportSecurity(Func<X509Certificate2> certificate, Func<string, string, SetupResponse> setup = null, Func<TcpClient, bool> allowConnection = null) {
            if (certificate == null) throw new ArgumentNullException("certificate");
            Certificate = certificate;
            Setup = setup;
            AllowConnection = allowConnection;
        }
    }
    // Small bounded HTTP/WebSocket gateway. No HTTP.sys URL reservation, admin
    // rights, external web server, filesystem routes or Internet requests.
    internal static class WebGateway
    {
        public static Stream Open(TcpClient client, TransportSecurity security = null)
        {
            if (security != null && security.AllowConnection != null && !security.AllowConnection(client)) return null;
            Stream stream = client.GetStream();
            int first = stream.ReadByte();
            bool encrypted = false;
            try {
                if (security != null && first == 22) {
                    var tls = new SslStream(new PrefixStream(stream, (byte)first), false);
                    stream = tls;
                    tls.ReadTimeout = 8000; tls.WriteTimeout = 3000;
                    tls.AuthenticateAsServer(security.Certificate(), false, SslProtocols.Tls12, false);
                    if (!tls.IsEncrypted || !tls.IsSigned || tls.CipherStrength < 128) throw new AuthenticationException("Encrypted TLS required");
                    encrypted = true; first = stream.ReadByte();
                }
                if (first < 0 || (security != null && !encrypted && first != 'G')) { stream.Dispose(); return null; }
                if (first != 'G') return new PrefixStream(stream, (byte)first);
                Stream result = OpenHttp(client, stream, first, security, encrypted);
                if (result == null) stream.Dispose();
                return result;
            } catch { stream.Dispose(); throw; }
        }
        private static Stream OpenHttp(TcpClient client, Stream stream, int first, TransportSecurity security, bool encrypted)
        {
            string request = ReadHeaders(stream, first);
            string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] start = lines[0].Split(' ');
            if (start.Length != 3 || start[0] != "GET" || start[2] != "HTTP/1.1")
            { Reply(stream, "400 Bad Request", "text/plain", Encoding.UTF8.GetBytes("Invalid request")); return null; }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0 || headers.ContainsKey(lines[i].Substring(0, colon))) throw new IOException("Invalid header");
                headers.Add(lines[i].Substring(0, colon), lines[i].Substring(colon + 1).Trim());
            }
            string host;
            Uri address;
            IPAddress ip;
            if (!headers.TryGetValue("Host", out host) || !Uri.TryCreate((encrypted ? "https://" : "http://") + host, UriKind.Absolute, out address) ||
                !IPAddress.TryParse(address.Host, out ip) || address.AbsolutePath != "/" || address.UserInfo.Length != 0 ||
                address.Query.Length != 0 || address.Fragment.Length != 0 || address.Port != ((IPEndPoint)client.Client.LocalEndPoint).Port ||
                !ip.Equals(((IPEndPoint)client.Client.LocalEndPoint).Address))
            { Reply(stream, "403 Forbidden", "text/plain", Encoding.UTF8.GetBytes("Open the numeric IP address shown on the laptop.")); return null; }
            string preferences;
            headers.TryGetValue("Accept-Language", out preferences);
            string language = UiText.Language(preferences);
            if (security != null && security.Setup != null && (!encrypted || (start[1] != "/" && start[1] != "/index.html"))) {
                var setup = security.Setup(start[1], "https://" + host);
                if (setup != null) {
                    byte[] body = setup.ContentType.StartsWith("text/html", StringComparison.Ordinal) || start[1] == "/remote.mobileconfig"
                        ? Encoding.UTF8.GetBytes(UiText.Html(Encoding.UTF8.GetString(setup.Body), language)) : setup.Body;
                    Reply(stream, "200 OK", setup.ContentType, body); return null;
                }
            }
            if (security != null && !encrypted) {
                Reply(stream, "426 Upgrade Required", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(string.Format(UiText.Translate("Откройте {0}. Незашифрованное управление отключено.", language), "https://" + host + "/"))); return null;
            }
            if (start[1] == "/ws")
            {
                string origin, upgrade, connection, key, version;
                Uri originAddress;
                if (!headers.TryGetValue("Origin", out origin) || !Uri.TryCreate(origin, UriKind.Absolute, out originAddress) ||
                    originAddress.GetLeftPart(UriPartial.Authority) != address.GetLeftPart(UriPartial.Authority) ||
                    !headers.TryGetValue("Upgrade", out upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
                    !headers.TryGetValue("Connection", out connection) || !HasToken(connection, "upgrade") ||
                    !headers.TryGetValue("Sec-WebSocket-Version", out version) || version != "13" ||
                    !headers.TryGetValue("Sec-WebSocket-Key", out key) || !ValidKey(key))
                { Reply(stream, "403 Forbidden", "text/plain", Encoding.UTF8.GetBytes("WebSocket origin or handshake rejected")); return null; }
                string accept;
                using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                stream.Write(response, 0, response.Length);
                return new WebSocketStream(stream);
            }
            string file, contentType;
            switch (start[1])
            {
                case "/locale.js": Reply(stream, "200 OK", "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(UiText.BrowserScript())); return null;
                case "/": case "/index.html": file = "index.html"; contentType = "text/html; charset=utf-8"; break;
                case "/app.css": file = "app.css"; contentType = "text/css; charset=utf-8"; break;
                case "/app.js": file = "app.js"; contentType = "text/javascript; charset=utf-8"; break;
                case "/viewport.js": file = "viewport.js"; contentType = "text/javascript; charset=utf-8"; break;
                case "/keyboard.js": file = "keyboard.js"; contentType = "text/javascript; charset=utf-8"; break;
                case "/gestures.js": file = "gestures.js"; contentType = "text/javascript; charset=utf-8"; break;
                case "/screen.js": file = "screen.js"; contentType = "text/javascript; charset=utf-8"; break;
                case "/manifest.webmanifest": file = "manifest.webmanifest"; contentType = "application/manifest+json"; break;
                case "/apple-touch-icon-v2.png": file = "apple-touch-icon-v2.png"; contentType = "image/png"; break;
                case "/remote-icon-v2.png": file = "remote-icon-v2.png"; contentType = "image/png"; break;
                case "/icon.svg": case "/favicon.ico": file = "icon.svg"; contentType = "image/svg+xml"; break;
                default: Reply(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found")); return null;
            }
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web." + file))
            {
                if (resource == null) throw new IOException("Missing bundled asset");
                using (var data = new MemoryStream())
                {
                    resource.CopyTo(data);
                    byte[] body = data.ToArray();
                    if (file == "index.html") body = Encoding.UTF8.GetBytes(UiText.Html(Encoding.UTF8.GetString(body), language));
                    if (file == "manifest.webmanifest") {
                        var json = new JavaScriptSerializer();
                        var manifest = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                        manifest["lang"] = language;
                        manifest["name"] = UiText.Translate((string)manifest["name"], language);
                        body = Encoding.UTF8.GetBytes(json.Serialize(manifest));
                    }
                    Reply(stream, "200 OK", contentType, body);
                }
            }
            return null;
        }

        private static bool HasToken(string list, string token)
        { foreach (string value in list.Split(',')) if (value.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private static bool ValidKey(string key)
        { try { return Convert.FromBase64String(key).Length == 16; } catch (FormatException) { return false; } }
        private static string ReadHeaders(Stream stream, int first)
        {
            var data = new List<byte> { (byte)first };
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (data.Count < 8192)
            {
                if (DateTime.UtcNow > deadline) throw new IOException("HTTP timeout");
                int value = stream.ReadByte();
                if (value < 0 || value > 127) throw new IOException("Invalid HTTP header");
                data.Add((byte)value);
                int n = data.Count;
                if (n >= 4 && data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10)
                    return Encoding.ASCII.GetString(data.ToArray());
            }
            throw new IOException("HTTP headers too large");
        }
        private static void Reply(Stream stream, string status, string type, byte[] body)
        {
            string header = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY" +
                "\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; img-src 'self' blob:; connect-src 'self' ws:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(header);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
        }
    }

    internal class PrefixStream : Stream
    {
        protected readonly Stream Inner;
        private int first;
        public PrefixStream(Stream inner, byte first) { Inner = inner; this.first = first; }
        public override int ReadByte() { if (first < 0) return Inner.ReadByte(); int value = first; first = -1; return value; }
        public override int Read(byte[] buffer, int offset, int count)
        { if (count == 0) return 0; if (first >= 0) { buffer[offset] = (byte)ReadByte(); return 1; } return Inner.Read(buffer, offset, count); }
        public override void Write(byte[] buffer, int offset, int count) { Inner.Write(buffer, offset, count); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanTimeout { get { return Inner.CanTimeout; } }
        public override int ReadTimeout { get { return Inner.ReadTimeout; } set { Inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return Inner.WriteTimeout; } set { Inner.WriteTimeout = value; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { Inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) Inner.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class WebSocketStream : PrefixStream
    {
        private byte[] pending = new byte[0];
        private int position;
        private readonly MemoryStream fragments = new MemoryStream();
        private bool continuing;
        private readonly object writeGate = new object();
        public WebSocketStream(Stream stream) : base(stream, 0) { }

        public override int ReadByte()
        {
            while (position == pending.Length)
            {
                if (!NextMessage()) return -1;
            }
            return pending[position++];
        }
        public override int Read(byte[] buffer, int offset, int count)
        { if (count == 0) return 0; int value = ReadByte(); if (value < 0) return 0; buffer[offset] = (byte)value; return 1; }

        private bool NextMessage()
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (true)
            {
                if (DateTime.UtcNow > deadline) throw new IOException("WebSocket frame timeout");
                int first = Inner.ReadByte();
                if (first < 0) return false;
                int second = RequiredByte();
                bool final = (first & 128) != 0;
                int opcode = first & 15;
                if ((first & 112) != 0 || (second & 128) == 0) throw new IOException("Invalid WebSocket frame");
                int length = second & 127;
                if (length == 127) throw new IOException("WebSocket frame too large");
                if (length == 126) { length = RequiredByte() * 256 + RequiredByte(); if (length < 126) throw new IOException("Noncanonical frame length"); }
                bool control = opcode >= 8;
                if (length > 1024 || (control && (!final || length > 125))) throw new IOException("Invalid frame size");
                byte[] mask = new byte[4];
                for (int i = 0; i < 4; i++) mask[i] = (byte)RequiredByte();
                byte[] body = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    if (DateTime.UtcNow > deadline) throw new IOException("WebSocket frame timeout");
                    body[i] = (byte)(RequiredByte() ^ mask[i % 4]);
                }
                if (opcode == 8) { SendFrame(8, body, 0, body.Length); return false; }
                if (opcode == 9) { SendFrame(10, body, 0, body.Length); continue; }
                if (opcode == 10) continue;
                if ((opcode != 0 && opcode != 1) || (opcode == 0 && !continuing) || (opcode == 1 && continuing))
                    throw new IOException("Expected a text message");
                if (fragments.Length + length > 1024) throw new IOException("Message too large");
                fragments.Write(body, 0, body.Length);
                continuing = !final;
                if (!final) continue;
                byte[] message = fragments.ToArray();
                // Validate UTF-8 and prevent multiple commands in a single browser message.
                new UTF8Encoding(false, true).GetString(message);
                if (Array.IndexOf(message, (byte)10) >= 0) throw new IOException("Expected one JSON object");
                pending = new byte[message.Length + 1];
                Array.Copy(message, pending, message.Length);
                pending[message.Length] = 10;
                position = 0;
                fragments.SetLength(0);
                return true;
            }
        }
        private int RequiredByte() { int value = Inner.ReadByte(); if (value < 0) throw new IOException("Unexpected EOF"); return value; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0 && buffer[offset + count - 1] == 10) count--;
            SendFrame(1, buffer, offset, count);
        }
        private void SendFrame(byte opcode, byte[] buffer, int offset, int count)
        {
            lock (writeGate) {
            byte[] header;
            if (count < 126) header = new[] { (byte)(128 | opcode), (byte)count };
            else if (count <= 65535) header = new[] { (byte)(128 | opcode), (byte)126, (byte)(count >> 8), (byte)count };
            else { header = new byte[10]; header[0] = (byte)(128 | opcode); header[1] = 127; for (int i = 0; i < 8; i++) header[i + 2] = (byte)((ulong)count >> (56 - i * 8)); }
            // One socket write avoids a tiny header packet and delayed TCP acknowledgements.
            byte[] packet = new byte[header.Length + count];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            Buffer.BlockCopy(buffer, offset, packet, header.Length, count);
            Inner.Write(packet, 0, packet.Length);
            }
        }
        public void SendBinary(byte[] data) { SendFrame(2, data, 0, data.Length); }
    }
}
