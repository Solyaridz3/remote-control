using System;
using System.Threading;
using RemoteController;

internal sealed class PreviewInput : IInput, IPinchInput, ITextInput, IDragInput
{
    public bool PinchSupported { get { return true; } }
    public void SetLeftButton(bool down) { Console.WriteLine("INPUT drag " + (down ? "start" : "end")); }
    public void CancelDrag() { }
    public void Pinch(string phase, int scale) { Console.WriteLine("INPUT pinch " + phase + " " + scale); }
    public void CancelGestures() { }
    public void Move(int x, int y) { Console.WriteLine("INPUT move " + x + " " + y); }
    public void Click(bool right) { Console.WriteLine("INPUT click " + (right ? "right" : "left")); }
    public void Scroll(int delta) { Console.WriteLine("INPUT scroll " + delta); }
    public void Volume(string action) { Console.WriteLine("INPUT volume " + action); }
    public void EditText(int deletions, string text) { Console.WriteLine("INPUT text edit: deleted=" + deletions + ", inserted=" + text.Length); }
}
internal static class BrowserPreview
{
    private static void Main(string[] args)
    {
        using (var magnifier = args.Length > 0 && args[0] == "--magnifier" ? new MagnifierService(true) : null)
        using (var server = new RemoteServer(new PreviewInput(), new PairingStore(null), magnifier,
            Array.IndexOf(args, "--screen") >= 0 ? (Func<IScreenSource>)(() => new DesktopScreenSource()) : () => new PreviewScreen()))
        {
            server.Start(49733);
            Console.WriteLine("Preview: http://127.0.0.1:" + server.Port + "/");
            Console.WriteLine("Pairing code: " + server.Code);
            Console.WriteLine("FAKE INPUT ONLY. No real mouse, clicks or volume changes.");
            using (var stop = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; stop.Set(); };
                stop.WaitOne();
            }
        }
    }
}
