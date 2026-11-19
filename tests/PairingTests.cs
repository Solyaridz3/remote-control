using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using RemoteController;

internal static class PairingTests
{
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static string TokenHello(PairingCredential credential) { return Json.Serialize(new { type = "hello", version = 1, deviceId = credential.Id, token = credential.Token }); }
    private static Dictionary<string, object> Read(Peer peer) { return Json.Deserialize<Dictionary<string, object>>(peer.Read()); }
    private static string TemporaryFile() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pairing-test-" + Guid.NewGuid().ToString("N") + ".json"); }
    public static int Run()
    {
        Test("Remembered device survives process restart; only hash is stored", () => {
            string path = TemporaryFile();
            try {
                var store = new PairingStore(path); var credential = store.Register("Мой iPhone");
                Check(!File.ReadAllText(path).Contains(credential.Token), "Raw credential stored on Windows");
                Check(!File.ReadAllText(path).Contains("Мой iPhone"), "Device name stored without encryption");
                var restored = new PairingStore(path);
                Check(restored.Authenticate(credential.Id, credential.Token), "Credential lost after restart");
                Check(restored.List()[0].Name == "Мой iPhone", "Device name lost");
                Check(!restored.Authenticate(credential.Id, new string('0', 64)), "Incorrect token accepted");
                Check(!restored.Authenticate(new string('a', 32), credential.Token), "Incorrect device accepted");
            } finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Legacy saved devices migrate to DPAPI without changing credentials", () => {
            string path=TemporaryFile();
            try {
                var credential=new PairingStore(null).Register("Legacy phone");string hash;
                using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(credential.Token))).Replace("-","");
                File.WriteAllText(path,Json.Serialize(new PairingStore.Document {Version=1,Devices=new List<PairingStore.Record>{new PairingStore.Record {Id=credential.Id,Name="Legacy phone",CreatedUtc=DateTime.UtcNow.ToString("o"),TokenHash=hash}}}));
                var migrated=new PairingStore(path);Check(migrated.LoadError==null&&migrated.Authenticate(credential.Id,credential.Token),"Migration lost access");
                byte[] encrypted=File.ReadAllBytes(path);Check(Encoding.ASCII.GetString(encrypted).StartsWith("REMOTE-PAIRING-DPAPI-1\n")&&!Encoding.UTF8.GetString(encrypted).Contains("Legacy phone"),"Migration did not protect data");
                Check(new PairingStore(path).Authenticate(credential.Id,credential.Token),"Protected credentials did not persist");
                encrypted[encrypted.Length-1]^=1;File.WriteAllBytes(path,encrypted);
                var corrupt=new PairingStore(path);Check(corrupt.LoadError!=null&&!corrupt.Authenticate(credential.Id,credential.Token),"Damaged ciphertext authenticated");
                Check(File.ReadAllBytes(path).SequenceEqual(encrypted),"Damaged ciphertext overwritten");
            }finally{if(File.Exists(path))File.Delete(path);}
        });
        Test("Pairing names are bounded and credentials are unique", () => {
            var store = new PairingStore(null); var a = store.Register("\n" + new string('x', 80)); var b = store.Register("");
            Check(a.Token.Length == 64 && a.Token != b.Token && a.Id != b.Id, "Invalid generated keys");
            Check(store.List()[0].Name.Length == 40 && store.List()[1].Name == "iPhone / Safari", "Name validation failed");
            for (int i = 2; i < 32; i++) store.Register("Phone");
            try { store.Register("Overflow"); throw new Exception("Unbounded device enrollment"); } catch (InvalidOperationException) { }
        });
        Test("Corrupt pairing file fails closed without overwriting original", () => {
            string path = TemporaryFile();
            try {
                File.WriteAllText(path, "broken-json"); var store = new PairingStore(path);
                Check(store.LoadError != null && store.List().Length == 0, "Corrupt store not reported");
                Check(!store.Authenticate(new string('a', 32), new string('B', 64)), "Corrupt store authenticated");
                try { store.Register("Phone"); throw new Exception("Corrupt store overwritten"); } catch (IOException) { }
                Check(File.ReadAllText(path) == "broken-json", "Original store lost");
                using (var server = new RemoteServer(new FakeInput(), store)) {
                    server.Start(0); using (var peer = new Peer(server.Port)) { peer.Hello(server.Code); Check(peer.Read().Contains("ready"), "Code-only access lost"); }
                }
            } finally { File.Delete(path); }
        });
        Test("Failed disk save cannot leave a trusted device in memory", () => {
            string blocker = TemporaryFile(); File.WriteAllText(blocker, "block-directory-creation");
            try {
                var store = new PairingStore(Path.Combine(blocker, "devices.json"));
                try { store.Register("Phone"); throw new Exception("Expected write failure"); } catch (IOException) { }
                Check(store.List().Length == 0, "Unpersisted device trusted");
            } finally { File.Delete(blocker); }
        });
        Test("Code-only session does not enroll a device", () => {
            var store = new PairingStore(null);
            using (var server = new RemoteServer(new FakeInput(), store)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); Check(!(bool)Read(peer)["remembered"], "Implicit enrollment");
                    Check(server.SavedDevices.Length == 0, "Unrequested credential created");
                }
            }
        });
        Test("WebSocket first pairing issues a key; next connection needs no code", () => {
            var store = new PairingStore(null);
            using (var server = new RemoteServer(new FakeInput(), store)) {
                server.Start(0); PairingCredential credential;
                using (var browser = new WebTests.Browser(server)) {
                    browser.Send(Json.Serialize(new { type = "hello", version = 1, code = server.Code, remember = true, deviceName = "Test iPhone" }));
                    var ready = Json.Deserialize<Dictionary<string, object>>(browser.Read());
                    Check((string)ready["type"] == "ready" && (bool)ready["remembered"], "Pairing failed");
                    credential = new PairingCredential { Id = (string)ready["deviceId"], Token = (string)ready["token"] };
                    browser.Send("{\"type\":\"disconnect\"}"); Check(browser.Closed(), "Owner not released");
                }
                server.RotateCode();
                using (var browser = new WebTests.Browser(server)) {
                    browser.Send(TokenHello(credential)); var ready = Json.Deserialize<Dictionary<string, object>>(browser.Read());
                    Check((string)ready["type"] == "ready" && ready["token"] == null, "Saved key did not authenticate, or was reissued");
                    browser.Send("{\"type\":\"ping\"}"); Check(browser.Read().Contains("pong"), "Saved session not usable");
                }
                Check(server.SavedDevices.Length == 1, "Reconnect created duplicate device");
            }
        });
        Test("Invalid saved key cannot fall back to a supplied correct code", () => {
            using (var server = new RemoteServer(new FakeInput())) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Send(Json.Serialize(new { type = "hello", version = 1, code = server.Code, deviceId = new string('a', 32), token = new string('B', 64) }));
                    Check((string)Read(peer)["code"] == "invalid_device", "Mixed authentication accepted");
                }
            }
        });
        Test("Forgetting a saved device revokes disk key and active pinch immediately", () => {
            string path = TemporaryFile();
            try {
                var store = new PairingStore(path); var credential = store.Register("Phone"); var input = new FakeInput();
                using (var server = new RemoteServer(input, store)) {
                    server.Start(0); using (var peer = new Peer(server.Port)) {
                        peer.Send(TokenHello(credential)); Check((string)Read(peer)["type"] == "ready", "Saved login failed");
                        peer.Send("{\"type\":\"pinch\",\"phase\":\"start\"}"); peer.Send("{\"type\":\"ping\"}"); peer.Read();
                        server.ForgetDevice(credential.Id);
                        Check((string)Read(peer)["code"] == "invalid_device" && peer.Closed(), "Active key not revoked");
                        Check(input.Events.Contains("pinch:cancel"), "Revocation left contacts active");
                    }
                    Check(server.SavedDevices.Length == 0, "Device still listed");
                }
                Check(!new PairingStore(path).Authenticate(credential.Id, credential.Token), "Revocation lost after restart");
            } finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Trusted key can connect during incorrect-code cooldown", () => {
            var store = new PairingStore(null); var credential = store.Register("Phone");
            using (var server = new RemoteServer(new FakeInput(), store)) {
                server.Start(0);
                for (int i = 0; i < 5; i++) using (var peer = new Peer(server.Port)) { peer.Hello("WRONG"); peer.Read(); }
                using (var peer = new Peer(server.Port)) { peer.Send(TokenHello(credential)); Check((string)Read(peer)["type"] == "ready", "Trusted key blocked by PIN failures"); }
            }
        });
        Test("Saved key respects exclusive owner and stays valid when busy", () => {
            var store = new PairingStore(null); var credential = store.Register("Phone");
            using (var server = new RemoteServer(new FakeInput(), store)) {
                server.Start(0); using (var owner = new Peer(server.Port)) using (var next = new Peer(server.Port)) {
                    owner.Hello(server.Code); owner.Read(); next.Send(TokenHello(credential));
                    Check((string)Read(next)["code"] == "busy", "Saved phone displaced owner");
                    Check(store.Authenticate(credential.Id, credential.Token), "Busy response revoked key");
                }
            }
        });
        return passed;
    }
}
