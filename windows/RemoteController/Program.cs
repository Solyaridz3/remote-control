using System;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace RemoteController
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form;
            try { form = new MainForm(); }
            catch (Exception ex) {
                if (!(ex is System.Security.Cryptography.CryptographicException || ex is System.IO.IOException || ex is UnauthorizedAccessException)) throw;
                MessageBox.Show("Не удалось подготовить защищённое подключение Remote. Сервер не запущен.\n\n" + ex.Message, "Remote", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }
            if (Array.IndexOf(args, "--minimized") >= 0) form.Shown += (sender, e) => { form.WindowState = FormWindowState.Minimized; };
            Application.Run(form);
        }
    }

    public sealed class MainForm : Form
    {
        private readonly MagnifierService magnifier = new MagnifierService();
        private readonly RemoteServer server;
        private readonly LocalTlsIdentity tlsIdentity;
        private readonly Label status = new Label();
        private readonly Label code = new Label();
        private readonly TextBox addresses = new TextBox();
        private readonly Button toggle = new Button();
        private bool listening;

        public MainForm()
        {
            tlsIdentity = new LocalTlsIdentity(LocalTlsIdentity.DefaultDirectory);
            server = new RemoteServer(new WindowsInput(), new PairingStore(PairingStore.DefaultPath), magnifier, () => new DesktopScreenSource(), new TransportSecurity(tlsIdentity.GetCertificate, tlsIdentity.PublicResponse));
            Text = "Remote · iPhone → Windows";
            ClientSize = new Size(660, 740);
            MinimumSize = new Size(580, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 23, 31);
            ForeColor = Color.FromArgb(234, 241, 247);
            Font = new Font("Segoe UI", 11);
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28), ColumnCount = 1, RowCount = 12 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(Label("REMOTE", 27, FontStyle.Bold));
            layout.Controls.Add(Label("Ваш iPhone — тачпад для ноутбука", 12, FontStyle.Regular));
            status.Text = "Подготовка…";
            status.ForeColor = Color.FromArgb(106, 227, 185);
            status.AutoSize = true;
            status.Margin = new Padding(0, 18, 0, 12);
            layout.Controls.Add(status);
            layout.Controls.Add(Label("ОТКРОЙТЕ НА IPHONE В SAFARI", 10, FontStyle.Bold));
            addresses.Multiline = true;
            addresses.ReadOnly = true;
            addresses.Height = 80;
            addresses.Dock = DockStyle.Top;
            addresses.BackColor = Color.FromArgb(30, 39, 51);
            addresses.ForeColor = ForeColor;
            addresses.BorderStyle = BorderStyle.FixedSingle;
            layout.Controls.Add(addresses);
            layout.Controls.Add(Label("КОД ПОДКЛЮЧЕНИЯ", 10, FontStyle.Bold));
            code.Font = new Font("Consolas", 25, FontStyle.Bold);
            code.AutoSize = true;
            code.Margin = new Padding(0, 8, 0, 14);
            code.Text = server.Code;
            layout.Controls.Add(code);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 10) };
            var copy = Button("Копировать код");
            copy.Click += (s, e) => Clipboard.SetText(server.Code);
            var rotate = Button("Отключить / новый код");
            rotate.Click += (s, e) => { server.RotateCode(); code.Text = server.Code; };
            buttons.Controls.Add(copy);
            buttons.Controls.Add(rotate);
            var copyLink = Button("Копировать ссылку");
            copyLink.Click += (s, e) => {
                string link = addresses.Lines.FirstOrDefault(a => a.StartsWith("https://"));
                if (link != null) Clipboard.SetText(link);
            };
            buttons.Controls.Add(copyLink);
            var secureSetup = Button("Защищённое подключение");
            secureSetup.Click += (s, e) => ShowSecureSetup();
            buttons.Controls.Add(secureSetup);
            layout.Controls.Add(buttons);
            toggle.Text = "Выключить приём";
            StyleButton(toggle);
            toggle.Click += (s, e) => { if (listening) { server.Stop(); listening = false; toggle.Text = "Включить приём"; } else StartServer(); };
            layout.Controls.Add(toggle);
            var devices = Button("Сохранённые устройства");
            devices.Click += (s, e) => ShowSavedDevices();
            layout.Controls.Add(devices);
            layout.Controls.Add(Label("1. Подключите iPhone и ноутбук к одной Wi-Fi сети.\n2. Нажмите «Защищённое подключение» для одноразовой настройки iPhone.\n3. Откройте HTTPS-ссылку и запомните телефон по коду.\nДалее подключение — автоматически, без повторного кода.", 11, FontStyle.Regular));
            var note = Label("Управление, текст и экран передаются по зашифрованному соединению.\nHTTP открывает только инструкцию установки сертификата.\nПри запросе брандмауэра разрешите только частные сети.\nАдреса обновляются автоматически. Закрытие окна выключает сервер.", 9, FontStyle.Regular);
            note.ForeColor = Color.FromArgb(165, 180, 198);
            note.Margin = new Padding(0, 16, 0, 0);
            layout.Controls.Add(note);
            server.Status += message =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke((Action)(() => { if (!IsDisposed) status.Text = message; })); }
                    catch (InvalidOperationException) { }
                }
            };
            var timer = new Timer { Interval = 5000 };
            timer.Tick += (s, e) => RefreshAddresses();
            Shown += (s, e) => { RefreshAddresses(); StartServer(); timer.Start(); };
            FormClosed += (s, e) => { timer.Dispose(); server.Dispose(); magnifier.Dispose(); tlsIdentity.Dispose(); };
        }

        private void ShowSecureSetup()
        {
            string link = addresses.Lines.FirstOrDefault(a => a.StartsWith("https://"));
            string setupLink = link == null ? "Подключите ноутбук к Wi-Fi, чтобы получить адрес настройки." : link.Replace("https://", "http://");
            using (var dialog = new Form { Text = "Remote — настройка HTTPS на iPhone", ClientSize = new Size(740, 610), StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor, Font = Font }) {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 2 };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = BackColor, ForeColor = ForeColor, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), Text =
                    "НАСТРОЙКА ОДИН РАЗ\r\n\r\n" +
                    "1. Откройте на iPhone в Safari адрес настройки:\r\n" + setupLink + "\r\n\r\n" +
                    "2. Скачайте профиль Remote. В сведениях сертификата профиля сверьте имя и весь отпечаток SHA-256 с данными ниже. Только после проверки установите профиль: Настройки → Основные → VPN и управление устройством.\r\n\r\n" +
                    "ИМЯ СЕРТИФИКАТА\r\n" + tlsIdentity.AuthorityName + "\r\n\r\nSHA-256\r\n" + tlsIdentity.Fingerprint + "\r\n\r\n" +
                    "3. Настройки → Основные → Об этом устройстве → Доверие сертификатам → включите полное доверие только проверенному сертификату Remote. Он добавляет локальный центр доверия; не устанавливайте чужие сертификаты.\r\n\r\n" +
                    "4. Откройте HTTPS-ссылку из главного окна Remote, введите код один раз и запомните устройство. Добавьте новый HTTPS-значок на экран Домой; старый HTTP-значок ведёт только на настройку.\r\n\r\n" +
                    "Не обходите предупреждения Safari. Если предупреждение осталось, проверьте сертификат, доверие и адрес. Удалить доверие можно удалением профиля Remote на iPhone. Закрытый ключ хранится только на этом ПК под защитой Windows; в профиль входит только публичный сертификат." };
                layout.Controls.Add(details);
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
                var copySetup = Button("Копировать адрес настройки"); copySetup.Enabled = link != null; copySetup.Click += (s, e) => Clipboard.SetText(setupLink); buttons.Controls.Add(copySetup);
                var export = Button("Сохранить профиль…"); export.Click += (s, e) => {
                    using (var save = new SaveFileDialog { FileName = "Remote.mobileconfig", Filter = "Профиль сертификата iPhone|*.mobileconfig" })
                        if (save.ShowDialog(dialog) == DialogResult.OK) System.IO.File.WriteAllBytes(save.FileName, tlsIdentity.PublicResponse("/remote.mobileconfig", "").Body);
                }; buttons.Controls.Add(export);
                var close = Button("Готово"); close.Click += (s, e) => dialog.Close(); buttons.Controls.Add(close);
                layout.Controls.Add(buttons); dialog.Controls.Add(layout); dialog.ShowDialog(this);
            }
        }

        private static Label Label(string text, int size, FontStyle style)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), Margin = new Padding(0, 3, 0, 7) };
        }
        private static Button Button(string text) { var button = new Button { Text = text }; StyleButton(button); return button; }
        private static void StyleButton(Button button)
        {
            button.AutoSize = true;
            button.Padding = new Padding(10, 6, 10, 6);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(35, 48, 63);
            button.ForeColor = Color.White;
            button.Margin = new Padding(0, 0, 10, 6);
        }
        private void RefreshAddresses()
        {
            try
            {
                var ips = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).Distinct().ToArray();
                addresses.Text = ips.Length == 0 ? "Нет IPv4 адреса. Подключитесь к Wi-Fi." : string.Join(Environment.NewLine, ips.Select(ip => "https://" + ip + ":" + RemoteServer.DefaultPort + "/").ToArray());
            }
            catch (NetworkInformationException) { addresses.Text = "Не удалось получить адреса сети."; }
        }
        private void StartServer()
        {
            try { server.Start(RemoteServer.DefaultPort); listening = true; toggle.Text = "Выключить приём"; }
            catch (SocketException ex) { status.Text = "Не удалось открыть порт: " + ex.Message; toggle.Text = "Повторить запуск"; }
        }
        private void ShowSavedDevices()
        {
            using (var dialog = new Form { Text = "Сохранённые устройства", ClientSize = new Size(560, 340),
                MinimumSize = new Size(460, 320), StartPosition = FormStartPosition.CenterParent,
                BackColor = BackColor, ForeColor = ForeColor, Font = Font })
            {
                var list = new ListBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 39, 51), ForeColor = ForeColor,
                    BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
                var help = Label(server.PairingError ?? "Телефон запоминается после первого подключения по коду.\n«Забыть» отзывает ключ и отключает это устройство.", 10, FontStyle.Regular);
                help.Dock = DockStyle.Top; help.Padding = new Padding(12); help.MaximumSize = new Size(540, 0);
                var remove = Button("Забыть выбранное устройство");
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12) };
                buttons.Controls.Add(remove);
                Action refresh = () => {
                    string selected = list.SelectedItem is PairedDevice ? ((PairedDevice)list.SelectedItem).Id : null;
                    var saved = server.SavedDevices;
                    if (string.Join("|", saved.Select(d => d.Id).ToArray()) != string.Join("|", list.Items.Cast<PairedDevice>().Select(d => d.Id).ToArray()))
                    {
                        list.BeginUpdate(); list.Items.Clear(); list.Items.AddRange(saved);
                        for (int i = 0; i < list.Items.Count; i++) if (((PairedDevice)list.Items[i]).Id == selected) list.SelectedIndex = i;
                        if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                        list.EndUpdate();
                    }
                    remove.Enabled = list.SelectedItem != null;
                };
                remove.Click += (s, e) => {
                    var selected = list.SelectedItem as PairedDevice;
                    if (selected == null) return;
                    try { server.ForgetDevice(selected.Id); refresh(); }
                    catch (Exception ex) {
                        if (!(ex is System.IO.IOException || ex is UnauthorizedAccessException)) throw;
                        MessageBox.Show(dialog, "Не удалось сохранить изменение. Проверьте доступ к файлу сохранённых устройств.", "Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                dialog.Controls.Add(list); dialog.Controls.Add(buttons); dialog.Controls.Add(help);
                using (var refreshTimer = new Timer { Interval = 2000 })
                {
                    refreshTimer.Tick += (s, e) => refresh(); refresh(); refreshTimer.Start(); dialog.ShowDialog(this);
                }
            }
        }
    }
}
