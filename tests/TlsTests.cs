using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using RemoteController;

internal static class TlsTests
{
    private sealed class SecureSocket : IDisposable
    {
        private readonly TcpClient client;
        private readonly SslStream tls;
        public SecureSocket(int port, X509Certificate2 certificate) {
            client = new TcpClient("127.0.0.1", port);client.ReceiveTimeout=3000;
            tls = new SslStream(client.GetStream(),false,(sender,cert,chain,errors)=>cert!=null && cert.GetCertHashString()==certificate.Thumbprint && (errors & SslPolicyErrors.RemoteCertificateNameMismatch)==0);
            tls.ReadTimeout=3000;tls.AuthenticateAsClient("127.0.0.1",null,SslProtocols.Tls12,false);
            Write("GET /ws HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nOrigin: https://127.0.0.1:"+port+"\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
            var header=new StringBuilder();while(!header.ToString().EndsWith("\r\n\r\n")){int b=tls.ReadByte();if(b<0||header.Length>8192)throw new Exception("TLS upgrade failed");header.Append((char)b);}
            Check(header.ToString().StartsWith("HTTP/1.1 101"),"WSS upgrade rejected");
        }
        private void Write(string text){byte[] data=Encoding.UTF8.GetBytes(text);tls.Write(data,0,data.Length);}
        public void Send(string json){byte[] data=Encoding.UTF8.GetBytes(json);using(var frame=new MemoryStream()){frame.WriteByte(129);if(data.Length<126)frame.WriteByte((byte)(128|data.Length));else{frame.WriteByte(254);frame.WriteByte((byte)(data.Length>>8));frame.WriteByte((byte)data.Length);}var mask=new byte[]{11,23,37,49};frame.Write(mask,0,4);for(int i=0;i<data.Length;i++)frame.WriteByte((byte)(data[i]^mask[i%4]));byte[] packet=frame.ToArray();tls.Write(packet,0,packet.Length);}}
        public byte[] Read(out int opcode){int first=tls.ReadByte(),second=tls.ReadByte();if(first<0||second<0)throw new IOException("WSS closed");opcode=first&15;long length=second&127;if(length==126)length=tls.ReadByte()*256+tls.ReadByte();else if(length==127){length=0;for(int i=0;i<8;i++)length=(length<<8)| (uint)tls.ReadByte();}Check(length>=0&&length<=2097152,"Invalid WSS frame size");byte[] data=new byte[(int)length];int position=0;while(position<data.Length){int n=tls.Read(data,position,data.Length-position);if(n==0)throw new IOException("Short frame");position+=n;}return data;}
        public string Read(){int opcode;byte[] data=Read(out opcode);Check(opcode==1,"Expected WSS text");return Encoding.UTF8.GetString(data);}
        public void Dispose(){tls.Dispose();client.Close();}
    }
    private sealed class Screen : IScreenSource {public byte[] Capture(int w,int h,int q){return new byte[70000];}public void Dispose(){}}
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Run()
    {
        int passed = 0;
        using (var key = new RSACng(2048)) {
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using (var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1)))
            using (var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx, "test-only"), "test-only", X509KeyStorageFlags.UserKeySet)) {
                var input = new FakeInput();
                using (var server = new RemoteServer(input, new PairingStore(null), null, null, new TransportSecurity(() => certificate))) {
                    server.Start(0);
                    using (var plain = new Peer(server.Port)) { plain.Raw("GET /app.js HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n"); Check(plain.Read().Contains("426"), "Plain HTTP exposed controller"); }
                    using (var plain = new Peer(server.Port)) { plain.Hello(server.Code); Check(plain.Closed(), "Plain input accepted"); }
                    Check(input.Count == 0, "Unencrypted input reached backend");
                    Console.WriteLine("PASS TLS server rejects plaintext assets and input"); passed++;
                    bool rejected=false;
                    using(var client=new TcpClient("127.0.0.1",server.Port))using(var untrusted=new SslStream(client.GetStream(),false,(sender,cert,chain,errors)=>false)) {
                        try{untrusted.AuthenticateAsClient("127.0.0.1",null,SslProtocols.Tls12,false);}catch(AuthenticationException){rejected=true;}catch(IOException){rejected=true;}
                    }
                    Check(rejected&&input.Count==0,"Rejected certificate still allowed a connection");
                    Console.WriteLine("PASS TLS refuses a certificate the client does not trust");passed++;
                    using (var client = new TcpClient("127.0.0.1", server.Port))
                    using (var tls = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) =>
                        cert != null && cert.GetCertHashString() == certificate.Thumbprint && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0)) {
                        client.ReceiveTimeout = 3000; tls.ReadTimeout = 3000;
                        tls.AuthenticateAsClient("127.0.0.1", null, SslProtocols.Tls12, false);
                        Check(tls.IsEncrypted && tls.IsSigned && tls.SslProtocol == SslProtocols.Tls12, "Secure TLS not negotiated");
                        var hello = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"version\":1,\"code\":\"" + server.Code + "\"}\n");
                        tls.Write(hello,0,hello.Length);
                        var reader = new StreamReader(tls,Encoding.UTF8);
                        Check(reader.ReadLine().Contains("ready"), "Encrypted pairing failed");
                        var move = Encoding.UTF8.GetBytes("{\"type\":\"move\",\"dx\":3,\"dy\":4}\n{\"type\":\"ping\"}\n");
                        tls.Write(move,0,move.Length);Check(reader.ReadLine().Contains("pong") && input.Count == 1,"Encrypted commands failed");
                    }
                    Console.WriteLine("PASS TLS 1.2 authenticates the server and encrypts pairing and control");passed++;
                }
            }
        }
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tls-test-"+Guid.NewGuid().ToString("N"));
        try {
            byte[] originalRoot;string fingerprint;
            using(var identity=new LocalTlsIdentity(directory)) {
                originalRoot=identity.PublicCertificate;fingerprint=identity.Fingerprint;
                using(var publicCert=new X509Certificate2(originalRoot))Check(!publicCert.HasPrivateKey,"Public export includes private key");
                using(var root=new X509Certificate2(originalRoot))using(var chain=new X509Chain()) {
                    chain.ChainPolicy.ExtraStore.Add(root);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;chain.ChainPolicy.VerificationFlags=X509VerificationFlags.AllowUnknownCertificateAuthority;
                    Check(chain.Build(identity.GetCertificate()),"Server certificate chain failed validation");
                    Check(chain.ChainElements[chain.ChainElements.Count-1].Certificate.RawData.SequenceEqual(originalRoot),"Unexpected TLS trust root");
                }
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory,"authority.dpapi"))).Contains(identity.AuthorityName),"Authority not protected at rest");
                using(var server=new RemoteServer(new FakeInput(),new PairingStore(null),null,()=>new Screen(),new TransportSecurity(identity.GetCertificate,identity.PublicResponse))) {
                    server.Start(0);
                    using(var setup=new Peer(server.Port)) {setup.Raw("GET / HTTP/1.1\r\nHost: 127.0.0.1:"+server.Port+"\r\n\r\n");Check(setup.Read().Contains("200"),"HTTP setup missing");}
                    var inputJson=new JavaScriptSerializer();
                    using(var control=new SecureSocket(server.Port,identity.GetCertificate())) {
                        control.Send(inputJson.Serialize(new{type="hello",version=1,code=server.Code}));Check(control.Read().Contains("ready"),"WSS pairing failed");
                        control.Send("{\"type\":\"screen_start\",\"id\":1}");
                        var reply=inputJson.Deserialize<Dictionary<string,object>>(control.Read());
                        using(var viewer=new SecureSocket(server.Port,identity.GetCertificate())) {
                            viewer.Send(inputJson.Serialize(new{type="screen_hello",ticket=(string)reply["ticket"]}));Check(viewer.Read().Contains("screen_connected"),"Encrypted screen handshake failed");
                            viewer.Send("{\"type\":\"screen_next\",\"seq\":1,\"width\":390,\"height\":681,\"quality\":80,\"fps\":60}");int opcode;byte[] frame=viewer.Read(out opcode);Check(opcode==2&&frame.Length==70000,"Encrypted screen frame failed");
                            control.Send("{\"type\":\"ping\"}");Check(control.Read().Contains("pong"),"WSS control blocked");
                        }
                    }
                    var profile=identity.PublicResponse("/remote.mobileconfig","");var doc=new System.Xml.XmlDocument();doc.XmlResolver=null;doc.LoadXml(Encoding.UTF8.GetString(profile.Body));
                    var certificateData=doc.SelectSingleNode("//data");Check(certificateData!=null&&Convert.FromBase64String(certificateData.InnerText).SequenceEqual(originalRoot),"Profile certificate mismatch");
                }
            }
            using(var restored=new LocalTlsIdentity(directory))Check(restored.PublicCertificate.SequenceEqual(originalRoot)&&restored.Fingerprint==fingerprint,"Updating/restarting changed the trust authority");
            Console.WriteLine("PASS Per-PC TLS identity persists protected keys, exports public-only profile and encrypts WSS screen/control");passed++;
        } finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
        return passed;
    }
}
