using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteController
{
    public interface IScreenSource : IDisposable
    {
        byte[] Capture(int width, int height, int quality);
    }
    public interface IInput
    {
        void Move(int x, int y);
        void Click(bool right);
        void Scroll(int delta);
        void Volume(string action);
    }

    public interface IPinchInput
    {
        bool PinchSupported { get; }
        void Pinch(string phase, int scale);
        void CancelGestures();
    }
    public interface ITextInput
    {
        void EditText(int deletions, string text);
    }
    public interface IDragInput
    {
        void SetLeftButton(bool down);
        void CancelDrag();
    }

    public interface IMagnifier
    {
        bool Active { get; }
        void Configure(bool enabled, int radius, int zoom, string mode);
        void Disable();
    }

    // Protocol v1: UTF-8 JSON, one object per line. Only an authenticated
    // connection can dispatch commands. No button-down state survives a session.
    public sealed class RemoteServer : IDisposable
    {
        public const int DefaultPort = 49732;
        private readonly IInput input;
        private readonly IPinchInput pinchInput;
        private readonly ITextInput textInput;
        private readonly IDragInput dragInput;
        private readonly IMagnifier magnifier;
        private readonly Func<IScreenSource> screenFactory;
        private readonly TransportSecurity security;
        private TcpClient screenClient;
        private string screenTicket;
        private readonly PairingStore pairings;
        private readonly object gate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private TcpListener listener;
        private TcpClient owner;
        private string ownerDeviceId;
        private Stream ownerStream;
        private bool running;
        private string code;
        private DateTime blockedUntil;
        private int failures;
        public event Action<string> Status;
        public int Port { get; private set; }
        public string Code { get { lock (gate) return code; } }

        public RemoteServer(IInput input) : this(input, new PairingStore(null)) { }
        public RemoteServer(IInput input, PairingStore pairings) : this(input, pairings, null) { }
        public RemoteServer(IInput input, PairingStore pairings, IMagnifier magnifier) : this(input, pairings, magnifier, null) { }
        public RemoteServer(IInput input, PairingStore pairings, IMagnifier magnifier, Func<IScreenSource> screenFactory) : this(input, pairings, magnifier, screenFactory, null) { }
        public RemoteServer(IInput input, PairingStore pairings, IMagnifier magnifier, Func<IScreenSource> screenFactory, TransportSecurity security)
        { this.input = input; this.pairings = pairings; this.magnifier = magnifier; this.screenFactory = screenFactory; this.security = security; pinchInput = input as IPinchInput; textInput = input as ITextInput; dragInput = input as IDragInput; RotateCode(); }
        private void CloseScreen()
        {
            screenTicket = null;
            if (screenClient != null) screenClient.Close();
            screenClient = null;
        }
        private void ReleaseDrag()
        {
            if (dragInput != null) { try { dragInput.CancelDrag(); } catch (System.ComponentModel.Win32Exception) { Notify("Windows не приняла отпускание кнопки мыши."); } }
        }
        public PairedDevice[] SavedDevices { get { return pairings.List(); } }
        public string PairingError { get { return pairings.LoadError; } }
        public void ForgetDevice(string id)
        {
            lock (gate)
            {
                if (!pairings.Forget(id)) return;
                if (ownerDeviceId == id) CloseOwner("invalid_device", "Доступ этого телефона отозван на ноутбуке. Подключитесь по коду заново.");
            }
            Notify("Доступ сохранённого устройства отозван");
        }
        private void CloseOwner(string reason, string message)
        {
            if (owner == null) return;
            CloseScreen();
            try { Send(ownerStream, new JavaScriptSerializer().Serialize(new { type = "error", code = reason, message = message })); }
            catch (IOException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
            if (pinchInput != null) pinchInput.CancelGestures();
            ReleaseDrag();
            if (magnifier != null) magnifier.Disable();
            owner.Close(); owner = null; ownerStream = null; ownerDeviceId = null;
        }

        public void RotateCode()
        {
            byte[] bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            lock (gate)
            {
                code = BitConverter.ToString(bytes).Replace("-", "");
                CloseOwner("disconnected", "Соединение остановлено на ноутбуке. Можно подключиться снова.");
            }
            Notify("Код обновлён. Ожидание iPhone");
        }

        public void Start(int port)
        {
            lock (gate)
            {
                if (running) return;
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start(8);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                running = true;
            }
            var activeListener = listener;
            Task.Run(() => AcceptLoop(activeListener));
            Notify("Ожидание iPhone");
        }

        private void AcceptLoop(TcpListener activeListener)
        {
            while (true)
            {
                TcpClient client;
                try { client = activeListener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (gate)
                {
                    if (!running || clients.Count >= 16)
                    { client.Close(); continue; }
                    clients.Add(client);
                }
                Task.Run(() => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            bool authenticated = false;
            bool pinching = false;
            bool pinchFailed = false;
            bool dragging = false;
            bool magnifierRequested = false;
            int lastTextSequence = 0;
            Stream activeStream = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 3000;
                var stream = WebGateway.Open(client, security);
                activeStream = stream;
                if (stream == null) return;
                var json = new JavaScriptSerializer { MaxJsonLength = 1024, RecursionLimit = 8 };
                while (true)
                {
                    string line = ReadFrame(stream);
                    if (line == null) break;
                    var message = json.Deserialize<Dictionary<string, object>>(line);
                    if (message == null) break;
                    string type = Text(message, "type");
                    if (!authenticated)
                    {
                        if (type == "screen_hello") { HandleScreen(client, stream, json, message); return; }
                        PairingCredential credential = null;
                        string deviceId = null;
                        lock (gate)
                        {
                            if (!running) break;
                            bool tokenAttempt = message.ContainsKey("deviceId") || message.ContainsKey("token");
                            bool trusted = type == "hello" && Text(message, "version") == "1" && tokenAttempt &&
                                pairings.Authenticate(Text(message, "deviceId"), Text(message, "token"));
                            if (!trusted && DateTime.UtcNow < blockedUntil)
                            {
                                Send(stream, "{\"type\":\"error\",\"code\":\"rate_limited\",\"message\":\"Слишком много попыток. Подождите 30 секунд\"}");
                                break;
                            }
                            if (type != "hello" || Text(message, "version") != "1" ||
                                (tokenAttempt ? !trusted : !EqualCode(Text(message, "code"), code)))
                            {
                                failures++;
                                if (failures >= 5) { blockedUntil = DateTime.UtcNow.AddSeconds(30); failures = 0; }
                                Send(stream, json.Serialize(new { type = "error", code = tokenAttempt ? "invalid_device" : "invalid_code",
                                    message = tokenAttempt ? "Устройство больше не сохранено. Введите код заново." : "Неверный код или версия протокола" }));
                                break;
                            }
                            if (owner != null)
                            {
                                Send(stream, "{\"type\":\"error\",\"code\":\"busy\",\"message\":\"Ноутбук уже подключён к другому телефону\"}");
                                break;
                            }
                            if (trusted) deviceId = Text(message, "deviceId");
                            else
                            {
                                object remember;
                                if (message.TryGetValue("remember", out remember) && remember is bool && (bool)remember)
                                {
                                    try { credential = pairings.Register(Text(message, "deviceName")); deviceId = credential.Id; }
                                    catch (Exception ex)
                                    {
                                        if (!(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)) throw;
                                        Send(stream, json.Serialize(new { type = "error", code = "storage_unavailable",
                                            message = "Не удалось запомнить устройство. Проверьте список устройств в Windows или снимите «Запомнить устройство»." }));
                                        break;
                                    }
                                }
                            }
                            owner = client;
                            ownerStream = stream;
                            ownerDeviceId = deviceId;
                            authenticated = true;
                            failures = 0;
                        }
                        Send(stream, json.Serialize(new { type = "ready", version = 1, name = Environment.MachineName,
                            pinch = pinchInput != null && pinchInput.PinchSupported, drag = dragInput != null, keyboard = textInput != null, magnifier = magnifier != null, screen = screenFactory != null && stream is WebSocketStream, remembered = deviceId != null,
                            deviceId = deviceId, token = credential == null ? null : credential.Token }));
                        Notify("iPhone подключён");
                        continue;
                    }
                    // Serialize revocation and input so a revoked owner cannot issue
                    // one last command after the Windows disconnect button is pressed.
                    lock (gate)
                    {
                        if (owner != client || !running) break;
                        if (type == "disconnect") break;
                        if (type == "ping") {
                            if (magnifierRequested && magnifier != null && !magnifier.Active) {
                                magnifierRequested = false;
                                Send(stream, json.Serialize(new { type = "notice", feature = "magnifier", message = "Лупа остановлена Windows. Попробуйте включить её снова." }));
                            }
                            Send(stream, "{\"type\":\"pong\"}");
                        }
                        else if (type == "screen_start")
                        {
                            if (screenFactory == null || !(stream is WebSocketStream)) throw new FormatException();
                            int requestId = Number(message, "id", 1, int.MaxValue);
                            CloseScreen();
                            byte[] secret = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
                            screenTicket = BitConverter.ToString(secret).Replace("-", "");
                            Send(stream, json.Serialize(new { type = "screen_ready", id = requestId, ticket = screenTicket }));
                        }
                        else if (type == "screen_stop") { CloseScreen(); Send(stream, "{\"type\":\"screen_stopped\"}"); }
                        else if (type == "magnifier")
                        {
                            object enabledValue;
                            if (!message.TryGetValue("enabled", out enabledValue) || !(enabledValue is bool)) throw new FormatException();
                            bool enabled = (bool)enabledValue;
                            int radius = Number(message, "radius", 80, 280), zoom = Number(message, "zoom", 150, 400);
                            string mode = Text(message, "mode");
                            if (mode != "center" && mode != "offset") throw new FormatException();
                            try {
                                if (magnifier == null) throw new System.ComponentModel.Win32Exception(50);
                                magnifier.Configure(enabled, radius, zoom, mode);
                                magnifierRequested = enabled;
                                Send(stream, json.Serialize(new { type = "magnifier_state", enabled = enabled }));
                            } catch (System.ComponentModel.Win32Exception) {
                                if (magnifier != null) magnifier.Disable();
                                magnifierRequested = false;
                                Send(stream, json.Serialize(new { type = "notice", feature = "magnifier", message = "Не удалось включить лупу Windows. Попробуйте снова." }));
                            }
                        }
                        else if (type == "text_edit")
                        {
                            int sequence = Number(message, "seq", 1, int.MaxValue);
                            int deletions = Number(message, "deletions", 0, 128);
                            object value;
                            if (sequence != (long)lastTextSequence + 1 || !message.TryGetValue("text", out value) || !(value is string)) throw new FormatException();
                            string text = (string)value;
                            if (!ValidKeyboardText(text)) throw new FormatException();
                            lastTextSequence = sequence;
                            try
                            {
                                if (textInput == null) throw new System.ComponentModel.Win32Exception(50);
                                textInput.EditText(deletions, text);
                                Send(stream, json.Serialize(new { type = "text_ack", seq = sequence }));
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                Send(stream, json.Serialize(new { type = "notice", feature = "keyboard", seq = sequence,
                                    message = "Windows не приняла ввод. Откройте нужное поле и начните ввод заново." }));
                            }
                        }
                        else if (type == "pinch")
                        {
                            if (dragging) throw new FormatException();
                            string phase = Text(message, "phase");
                            if (phase != "start" && phase != "update" && phase != "end") throw new FormatException();
                            if (phase == "start") { if (pinching) throw new FormatException(); pinchFailed = false; }
                            else if (pinchFailed) { if (phase == "end") pinchFailed = false; continue; }
                            else if (!pinching) throw new FormatException();
                            int scale = phase == "update" ? Number(message, "scale", 100, 4000) : 1000;
                            if (pinchInput == null || !pinchInput.PinchSupported)
                            {
                                pinchFailed = true;
                                Send(stream, json.Serialize(new { type = "notice", feature = "pinch", message = "Эта версия Windows не поддерживает виртуальный precision-тачпад." }));
                                continue;
                            }
                            try { pinchInput.Pinch(phase, scale); pinching = phase != "end"; }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                pinchInput.CancelGestures(); pinching = false; pinchFailed = true;
                                Send(stream, json.Serialize(new { type = "notice", feature = "pinch", message = "Windows не приняла жест растягивания. Проверьте поддержку жестов в приложении и обновления Windows." }));
                            }
                        }
                        else if (type == "drag")
                        {
                            string phase = Text(message, "phase");
                            if (dragInput == null || pinching || (phase != "start" && phase != "end") || (phase == "start") == dragging) throw new FormatException();
                            dragInput.SetLeftButton(phase == "start"); dragging = phase == "start";
                        }
                        else if (type == "move") input.Move(Number(message, "dx", -500, 500), Number(message, "dy", -500, 500));
                        else if (type == "click")
                        {
                            string button = Text(message, "button");
                            if (dragging) throw new FormatException();
                            if (button != "left" && button != "right") throw new FormatException();
                            input.Click(button == "right");
                        }
                        else if (type == "scroll") input.Scroll(Number(message, "delta", -1200, 1200));
                        else if (type == "volume")
                        {
                            string action = Text(message, "action");
                            if (action != "up" && action != "down" && action != "mute") throw new FormatException();
                            input.Volume(action);
                        }
                        else throw new FormatException();
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
            catch (System.ComponentModel.Win32Exception) { Notify("Windows заблокировала ввод. Проверьте активное окно."); }
            catch (System.Security.Authentication.AuthenticationException) { }
            finally
            {
                bool released = false;
                lock (gate)
                {
                    clients.Remove(client);
                    if (screenClient == client) screenClient = null;
                    if (owner == client) { CloseScreen(); if (pinchInput != null) pinchInput.CancelGestures(); ReleaseDrag(); if (magnifier != null) magnifier.Disable(); owner = null; ownerStream = null; ownerDeviceId = null; released = true; }
                }
                client.Close();
                if (activeStream != null) activeStream.Dispose();
                if (released) Notify("iPhone отключён. Ожидание подключения");
            }
        }

        private void HandleScreen(TcpClient client, Stream stream, JavaScriptSerializer json, Dictionary<string, object> hello)
        {
            var websocket = stream as WebSocketStream;
            string ticket = Text(hello, "ticket");
            lock (gate) {
                if (websocket == null || screenFactory == null || !running || owner == null || screenTicket == null || ticket.Length != 64 || !EqualCode(ticket, screenTicket) || screenClient != null) {
                    Send(stream, "{\"type\":\"error\",\"code\":\"screen_denied\",\"message\":\"Трансляция не разрешена текущим подключением.\"}"); return;
                }
                screenClient = client;
            }
            try {
                using (var capture = screenFactory()) using (var pacer = new ScreenPacer()) {
                    Send(stream, "{\"type\":\"screen_connected\"}");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    double lastCapture = -1000;
                    long sequence = 0;
                    while (true) {
                        string line = ReadFrame(stream); if (line == null) return;
                        var request = json.Deserialize<Dictionary<string, object>>(line);
                        if (request == null || Text(request, "type") != "screen_next") throw new FormatException();
                        int seq = Number(request, "seq", 1, int.MaxValue);
                        if (seq != ++sequence) throw new FormatException();
                        int width = Number(request, "width", 160, 960), height = Number(request, "height", 120, 1280);
                        int quality = Number(request, "quality", 45, 85), fps = Number(request, "fps", 10, 60);
                        lock (gate) { if (screenClient != client || screenTicket == null || !EqualCode(ticket, screenTicket) || owner == null || !running) return; }
                        double wait = 1000.0 / fps - (clock.Elapsed.TotalMilliseconds - lastCapture);
                        if (wait > 0) pacer.Wait(wait);
                        lastCapture = clock.Elapsed.TotalMilliseconds;
                        byte[] frame = capture.Capture(width, height, quality);
                        if (frame == null || frame.Length == 0 || frame.Length > 2097152) throw new IOException("Invalid screen frame");
                        lock (gate) { if (screenClient != client || owner == null || screenTicket == null) return; }
                        // This socket has its own worker: screen encoding/network writes never hold the input gate.
                        websocket.SendBinary(frame);
                    }
                }
            } catch (System.ComponentModel.Win32Exception) { SendScreenFailure(stream); }
            catch (System.Runtime.InteropServices.ExternalException) { SendScreenFailure(stream); }
        }
        private static void SendScreenFailure(Stream stream)
        {
            try { Send(stream, "{\"type\":\"error\",\"message\":\"Не удалось получить изображение экрана Windows. Выключите и включите трансляцию.\"}"); }
            catch (IOException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
        }
        private sealed class ScreenPacer : IDisposable
        {
            private IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
            public void Wait(double milliseconds) {
                if (timer != IntPtr.Zero) {
                    long due = -Math.Max(1L, (long)(milliseconds * 10000));
                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) && WaitForSingleObject(timer, 1000) == 0) return;
                }
                Thread.Sleep((int)Math.Ceiling(milliseconds));
            }
            public void Dispose() { if (timer != IntPtr.Zero) { CloseHandle(timer); timer = IntPtr.Zero; } }
            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes,string name,uint flags,uint access);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr argument,bool resume);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        }

        private static bool EqualCode(string a, string b)
        {
            a = a.ToUpperInvariant();
            int difference = a.Length ^ b.Length;
            for (int i = 0; i < b.Length; i++) difference |= b[i] ^ (i < a.Length ? a[i] : 0);
            return difference == 0;
        }

        private static string Text(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static int Number(Dictionary<string, object> obj, string key, int min, int max)
        {
            object raw;
            if (!obj.TryGetValue(key, out raw) || !(raw is int)) throw new FormatException();
            int value = (int)raw;
            if (value < min || value > max) throw new FormatException();
            return value;
        }
        private static bool ValidKeyboardText(string text)
        {
            if (text.Length > 128) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c)) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
                else if (char.IsLowSurrogate(c) || (char.IsControl(c) && c != '\n' && c != '\t')) return false;
            }
            return true;
        }

        private static string ReadFrame(Stream stream)
        {
            var bytes = new List<byte>();
            // Bound both bytes and time: slow peers cannot retain a slot indefinitely.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (bytes.Count <= 1024)
            {
                if (DateTime.UtcNow > deadline) throw new IOException("Frame timeout");
                int value = stream.ReadByte();
                if (value < 0) return null;
                if (value == 10) return new UTF8Encoding(false, true).GetString(bytes.ToArray());
                bytes.Add((byte)value);
            }
            throw new IOException("Frame too large");
        }

        private static void Send(Stream stream, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        private void Notify(string message) { var callback = Status; if (callback != null) callback(message); }

        public void Stop()
        {
            lock (gate)
            {
                running = false;
                CloseScreen();
                if (listener != null) listener.Stop();
                if (pinchInput != null) pinchInput.CancelGestures();
                ReleaseDrag();
                if (magnifier != null) magnifier.Disable();
                foreach (var client in clients) client.Close();
                owner = null;
                ownerStream = null; ownerDeviceId = null;
            }
            Notify("Приём подключений выключен");
        }

        public void Dispose() { Stop(); }
    }
}
