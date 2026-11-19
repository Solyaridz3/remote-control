using System;
using System.Linq;
using System.Web.Script.Serialization;
using RemoteController;

internal static class KeyboardTests
{
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    public static int Run()
    {
        Test("Unicode keyboard packets preserve Cyrillic, surrogate pairs and key releases", () => {
            var inputs = WindowsInput.TextInputs(1, "Я😀\n\t");
            Check(inputs.Length == 12 && inputs.All(i => i.type == 1), "Wrong keyboard packet count");
            Check(inputs[0].data.keyboard.key == 8 && inputs[1].data.keyboard.flags == 2, "Missing backspace release");
            Check(inputs[2].data.keyboard.key == 0 && inputs[2].data.keyboard.scan == 'Я' && inputs[2].data.keyboard.flags == 4, "Wrong Unicode input");
            Check(inputs[3].data.keyboard.flags == 6, "Missing Unicode key release");
            Check(char.IsHighSurrogate((char)inputs[4].data.keyboard.scan) && char.IsLowSurrogate((char)inputs[6].data.keyboard.scan), "Emoji surrogate pair lost");
            Check(inputs[8].data.keyboard.key == 13 && inputs[10].data.keyboard.key == 9, "Enter or Tab not represented as keys");
        });
        Test("Authenticated WebSocket keyboard edits are acknowledged in order", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new WebTests.Browser(server)) {
                    peer.Send(Json.Serialize(new { type = "hello", version = 1, code = server.Code }));
                    Check(peer.Read().Contains("\"keyboard\":true"), "Keyboard capability missing");
                    peer.Send(Json.Serialize(new { type = "text_edit", seq = 1, deletions = 0, text = "Привет 😀" }));
                    Check(peer.Read().Contains("text_ack"), "Edit not acknowledged");
                    peer.Send(Json.Serialize(new { type = "text_edit", seq = 2, deletions = 1, text = "мир" }));
                    Check(peer.Read().Contains("\"seq\":2"), "Wrong acknowledgement sequence");
                    Check(string.Join("|", input.Events) == "text:0:Привет 😀|text:1:мир", "Wrong keyboard commands");
                }
            }
        });
        Test("Keyboard commands require authentication", () => {
            var input = new FakeInput();
            using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Send(Json.Serialize(new { type = "text_edit", seq = 1, deletions = 0, text = "x" }));
                    Check(peer.Read().Contains("error") && input.Count == 0, "Unpaired text injected");
                }
            }
        });
        string[] invalid = {
            "{\"type\":\"text_edit\",\"seq\":2,\"deletions\":0,\"text\":\"x\"}",
            "{\"type\":\"text_edit\",\"seq\":1,\"deletions\":-1,\"text\":\"x\"}",
            "{\"type\":\"text_edit\",\"seq\":1,\"deletions\":129,\"text\":\"x\"}",
            "{\"type\":\"text_edit\",\"seq\":1,\"deletions\":true,\"text\":\"x\"}",
            "{\"type\":\"text_edit\",\"seq\":1,\"deletions\":0,\"text\":null}",
            "{\"type\":\"text_edit\",\"seq\":1,\"deletions\":0,\"text\":\"\\u0000\"}",
            Json.Serialize(new { type = "text_edit", seq = 1, deletions = 0, text = new string('x', 129) })
        };
        foreach (string frame in invalid) {
            string bad = frame;
            Test("Invalid keyboard edit is rejected", () => {
                var input = new FakeInput(); using (var server = new RemoteServer(input)) {
                    server.Start(0); using (var peer = new Peer(server.Port)) {
                        peer.Hello(server.Code); peer.Read(); peer.Send(bad); Check(peer.Closed(), "Invalid edit accepted");
                        Check(input.Count == 0, "Invalid edit reached native input");
                    }
                }
            });
        }
        Test("JSON Unicode normalization never delivers an unpaired surrogate to native input", () => {
            var input = new FakeInput(); using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read();
                    peer.Send("{\"type\":\"text_edit\",\"seq\":1,\"deletions\":0,\"text\":\"\\ud800\"}");
                    Check(peer.Read().Contains("text_ack"), "Normalized Unicode not acknowledged");
                    Check(input.Events.All(e => !e.Any(char.IsSurrogate)), "Unpaired surrogate reached native input");
                }
            }
        });
        Test("Duplicate keyboard sequence cannot type the same edit twice", () => {
            var input = new FakeInput(); using (var server = new RemoteServer(input)) {
                server.Start(0); using (var peer = new Peer(server.Port)) {
                    peer.Hello(server.Code); peer.Read(); string frame = Json.Serialize(new { type = "text_edit", seq = 1, deletions = 0, text = "once" });
                    peer.Send(frame); peer.Read(); peer.Send(frame); Check(peer.Closed(), "Duplicate sequence accepted"); Check(input.Count == 1, "Duplicate text injected");
                }
            }
        });
        return passed;
    }
}
