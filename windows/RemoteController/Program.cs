using System;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace RemoteController
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool startInTray = Array.IndexOf(args, "--minimized") >= 0;
            bool firstInstance;
            using (var openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RemoteController.OpenWindow", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (!startInTray) openWindow.Set();
                    return;
                }
                Run(startInTray, openWindow);
            }
        }

        private static void Run(bool startInTray, EventWaitHandle openWindow)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form;
            try { form = new MainForm(); }
            catch (Exception ex) {
                if (!(ex is System.Security.Cryptography.CryptographicException || ex is System.IO.IOException || ex is UnauthorizedAccessException)) throw;
                MessageBox.Show(UiText.Get("Не удалось подготовить защищённое подключение PocketPointer. Сервер не запущен.\n\n") + UiText.Get(ex.Message), "PocketPointer", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }
            using (form)
            using (var context = new ApplicationContext())
            {
                form.FormClosed += (sender, e) => context.ExitThread();
                form.Start(startInTray);
                var registration = ThreadPool.RegisterWaitForSingleObject(openWindow, (state, timedOut) =>
                {
                    try { form.BeginInvoke((Action)form.OpenWindow); }
                    catch (InvalidOperationException) { }
                }, null, Timeout.Infinite, false);
                try { Application.Run(context); }
                finally { registration.Unregister(null); }
            }
        }
    }

    public sealed class MainForm : Form
    {
        private const int UseImmersiveDarkMode = 20;
        private const int CaptionColor = 35;
        private const int CaptionTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private readonly MagnifierService magnifier = new MagnifierService();
        private readonly RemoteServer server;
        private readonly LocalTlsIdentity tlsIdentity;
        private readonly Label status = new Label();
        private readonly Label code = new Label();
        private readonly TextBox addresses = new TextBox();
        private readonly Button toggle = new Button();
        private readonly Timer addressTimer = new Timer { Interval = 5000 };
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly Icon appIcon = new Icon(typeof(MainForm), "RemoteController.ico");
        private DesktopTheme theme = new DesktopTheme(DesktopTheme.LoadDark());
        private bool statusError;
        private bool listening;
        private bool exiting;

        public MainForm()
        {
            tlsIdentity = new LocalTlsIdentity(LocalTlsIdentity.DefaultDirectory);
#if STORE
            var transport = new TransportSecurity(tlsIdentity.GetCertificate, tlsIdentity.PublicResponse, StoreRelease.AllowConnection);
#else
            var transport = new TransportSecurity(tlsIdentity.GetCertificate, tlsIdentity.PublicResponse);
#endif
            server = new RemoteServer(new WindowsInput(), new PairingStore(PairingStore.DefaultPath), magnifier, () => new DesktopScreenSource(), transport);
            Text = "PocketPointer · iPhone → Windows";
            Icon = appIcon;
            ClientSize = new Size(812, 542);
            MinimumSize = new Size(740, 510);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = theme.Canvas;
            ForeColor = theme.Ink;
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            HandleCreated += (s, e) => ApplyCaption(this);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(28, 24, 28, 20), ColumnCount = 2, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            scroll.Controls.Add(layout);
            Controls.Add(scroll);

            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1,
                Margin = new Padding(0, 0, 0, 24), TabIndex = 0 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var logoIcon = new Icon(appIcon, 48, 48);
            var logo = new PictureBox { Size = new Size(54, 54),
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 14, 0), TabStop = false };
            logo.Paint += (s, e) => e.Graphics.DrawIcon(logoIcon, logo.ClientRectangle);
            var brand = Column(Color.Transparent, Padding.Empty);
            var title = Label("PocketPointer", 28, FontStyle.Bold);
            title.Margin = Padding.Empty;
            brand.Controls.Add(title);
            var subtitle = Caption(UiText.Get("iPhone как тачпад для Windows"));
            brand.Controls.Add(subtitle);
            status.AutoSize = true;
            status.Anchor = AnchorStyles.Right;
            status.MaximumSize = new Size(245, 0);
            status.Margin = new Padding(16, 0, 0, 6);
            status.Padding = new Padding(12, 8, 12, 8);
            DesktopTheme.Round(status, 14);
            SetStatus("Подготовка…");
            var themeButton = Button(UiText.Get("Светлая тема"));
            themeButton.Anchor = AnchorStyles.Right;
            themeButton.Margin = Padding.Empty;
            var headerActions = Column(Color.Transparent, Padding.Empty);
            headerActions.Anchor = AnchorStyles.Right;
            headerActions.Controls.Add(status);
            headerActions.Controls.Add(themeButton);
            header.Controls.Add(logo, 0, 0);
            header.Controls.Add(brand, 1, 0);
            header.Controls.Add(headerActions, 2, 0);
            layout.Controls.Add(header, 0, 0);
            layout.SetColumnSpan(header, 2);

            var connection = new ThemeCard(new Padding(20, 17, 20, 14)) { TabIndex = 1 };
            connection.Controls.Add(Label(UiText.Get("Открыть на iPhone"), 15, FontStyle.Bold));
            var addressHint = Caption(UiText.Get("Откройте адрес в Safari в той же сети Wi-Fi."));
            connection.Controls.Add(addressHint);
            addresses.Multiline = true;
            addresses.ReadOnly = true;
            addresses.AccessibleName = UiText.Get("HTTPS-адрес PocketPointer");
            addresses.WordWrap = false;
            addresses.Dock = DockStyle.Fill;
            addresses.Font = new Font("Consolas", 11);
            addresses.BorderStyle = BorderStyle.None;
            var addressField = new Panel { Dock = DockStyle.Top, Height = 62,
                Padding = new Padding(12, 9, 10, 7), Margin = Padding.Empty };
            DesktopTheme.Round(addressField, 12);
            addressField.Controls.Add(addresses);
            var copyLink = Button(UiText.Get("Копировать ссылку"));
            copyLink.Click += (s, e) => {
                string link = addresses.Lines.FirstOrDefault(a => a.StartsWith("https://"));
                if (link != null) Clipboard.SetText(link);
            };
            var connectionBody = FieldAndActions();
            connection.Controls.Add(connectionBody);

            var pairing = new ThemeCard(new Padding(20, 17, 20, 14)) { TabIndex = 2 };
            pairing.Controls.Add(Label(UiText.Get("Код подключения"), 15, FontStyle.Bold));
            var codeHint = Caption(UiText.Get("Введите при первом подключении."));
            pairing.Controls.Add(codeHint);
            code.Font = new Font("Consolas", 23, FontStyle.Bold);
            code.Dock = DockStyle.Top;
            code.Height = 62;
            code.Padding = new Padding(12, 0, 0, 0);
            code.TextAlign = ContentAlignment.MiddleLeft;
            code.Margin = Padding.Empty;
            code.Text = server.Code;
            DesktopTheme.Round(code, 12);
            var copy = Button(UiText.Get("Копировать код"));
            copy.Click += (s, e) => Clipboard.SetText(server.Code);
            var rotate = Button(UiText.Get("Новый код"));
            rotate.AccessibleDescription = UiText.Get("Сменить код и отключить текущее подключение");
            rotate.Click += (s, e) => { server.RotateCode(); code.Text = server.Code; };
            var pairActions = Actions(copy, rotate);
            var pairingBody = FieldAndActions();
            pairing.Controls.Add(pairingBody);

            var settings = new ThemeCard(new Padding(16, 12, 16, 8)) { Margin = Padding.Empty, TabIndex = 3 };
            var secureSetup = Button(UiText.Get("Защищённое подключение"));
            secureSetup.Click += (s, e) => ShowSecureSetup();
            var devices = Button(UiText.Get("Сохранённые устройства"));
            devices.Click += (s, e) => ShowSavedDevices();
            toggle.Text = UiText.Get("Выключить приём");
            StyleButton(toggle);
            toggle.Click += (s, e) => { if (listening) { server.Stop(); listening = false; toggle.Text = UiText.Get("Включить приём"); } else StartServer(); };
            settings.Controls.Add(QuickActions(secureSetup, devices, toggle));
            layout.Controls.Add(settings, 0, 3);
            layout.SetColumnSpan(settings, 2);
            var note = Caption(UiText.Get("После закрытия окна PocketPointer остаётся в трее. Для остановки выберите «Выход» в меню значка."));
            note.MaximumSize = new Size(660, 0);
            note.Margin = new Padding(0, 13, 0, 0);
            layout.Controls.Add(note, 0, 4);
            layout.SetColumnSpan(note, 2);

            Action applyTheme = () =>
            {
                layout.SuspendLayout();
                BackColor = theme.Canvas;
                ForeColor = theme.Ink;
                ApplyCaption(this);
                foreach (var caption in new[] { subtitle, addressHint, codeHint, note }) caption.ForeColor = theme.Muted;
                addressHint.Visible = codeHint.Visible = theme.Dark;
                connection.Apply(theme, theme.Dark);
                pairing.Apply(theme);
                settings.Apply(theme);
                addressField.BackColor = addresses.BackColor = code.BackColor = theme.Field;
                addresses.ForeColor = theme.Ink;
                ApplyButtonColors(copyLink, true);
                foreach (var button in new[] { copy, rotate, secureSetup, devices, toggle, themeButton }) ApplyButtonColors(button);
                themeButton.Text = theme.Dark ? UiText.Get("Светлая тема") : UiText.Get("Тёмная тема");
                themeButton.AccessibleDescription = theme.Dark ? UiText.Get("Текущая тема: тёмная") : UiText.Get("Текущая тема: светлая");
                UpdateStatusColors();

                ArrangeFields(connectionBody, addressField, copyLink, theme.Dark);
                ArrangeFields(pairingBody, code, pairActions, theme.Dark);
                layout.Controls.Remove(connection);
                layout.Controls.Remove(pairing);
                connection.Dock = pairing.Dock = theme.Dark ? DockStyle.Fill : DockStyle.Top;
                addressField.Height = code.Height = theme.Dark ? 62 : 50;
                connection.Margin = new Padding(0, 0, theme.Dark ? 8 : 0, 14);
                pairing.Margin = new Padding(theme.Dark ? 8 : 0, 0, 0, 14);
                layout.Controls.Add(connection, 0, 1);
                layout.SetColumnSpan(connection, theme.Dark ? 1 : 2);
                layout.Controls.Add(pairing, theme.Dark ? 1 : 0, theme.Dark ? 1 : 2);
                layout.SetColumnSpan(pairing, theme.Dark ? 1 : 2);
                layout.ResumeLayout(true);
            };
            applyTheme();
            themeButton.Click += (s, e) =>
            {
                theme = new DesktopTheme(!theme.Dark);
                applyTheme();
                theme.Save();
            };
            ActiveControl = copyLink;
            server.Status += message =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke((Action)(() => { if (!IsDisposed) SetStatus(message); })); }
                    catch (InvalidOperationException) { }
                }
            };
            addressTimer.Tick += (s, e) => RefreshAddresses();
            trayMenu.Items.Add(UiText.Get("Открыть PocketPointer"), null, (s, e) => OpenWindow());
#if STORE
            trayMenu.Items.Add(UiText.Get("Автозагрузка Windows"), null, (s, e) =>
                OpenStoreLink(StoreRelease.OpenStartupSettings, "Не удалось открыть настройки автозагрузки Windows."));
            trayMenu.Items.Add(UiText.Get("Конфиденциальность"), null, (s, e) =>
                OpenStoreLink(StoreRelease.OpenPrivacyPolicy, "Не удалось открыть политику конфиденциальности."));
#endif
            trayMenu.Items.Add(UiText.Get("Выход"), null, (s, e) => { exiting = true; Close(); });
            var smallIcon = new Icon(appIcon, SystemInformation.SmallIconSize);
            trayIcon.Icon = smallIcon;
            trayIcon.Text = "PocketPointer";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += (s, e) =>
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            FormClosed += (s, e) =>
            {
                trayIcon.Dispose();
                trayMenu.Dispose();
                smallIcon.Dispose();
                logoIcon.Dispose();
                appIcon.Dispose();
                addressTimer.Dispose();
                server.Dispose();
                magnifier.Dispose();
                tlsIdentity.Dispose();
            };
        }

        internal void Start(bool startInTray)
        {
            // Create the handle for server status and activation callbacks before showing the form.
            if (!IsHandleCreated) CreateHandle();
            RefreshAddresses();
            StartServer();
            addressTimer.Start();
            trayIcon.Visible = true;
            if (!startInTray) OpenWindow();
        }

        internal void OpenWindow()
        {
            if (exiting || IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void ShowSecureSetup()
        {
            string link = addresses.Lines.FirstOrDefault(a => a.StartsWith("https://"));
            string setupLink = link == null ? UiText.Get("Подключите ноутбук к Wi-Fi, чтобы получить адрес настройки.") : link.Replace("https://", "http://");
            using (var dialog = new Form { Text = UiText.Get("PocketPointer — настройка HTTPS на iPhone"), ClientSize = new Size(740, 610), StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor, Font = Font }) {
                dialog.Icon = appIcon;
                dialog.HandleCreated += (s, e) => ApplyCaption(dialog);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 2 };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = theme.Field, ForeColor = ForeColor, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), Text =
                    UiText.Get("НАСТРОЙКА ОДИН РАЗ\r\n\r\n") +
                    UiText.Get("1. Откройте на iPhone в Safari адрес настройки:\r\n") + setupLink + "\r\n\r\n" +
                    UiText.Get("2. Скачайте профиль PocketPointer. В сведениях сертификата профиля сверьте имя и весь отпечаток SHA-256 с данными ниже. Только после проверки установите профиль: Настройки → Основные → VPN и управление устройством.\r\n\r\n") +
                    UiText.Get("ИМЯ СЕРТИФИКАТА\r\n") + tlsIdentity.AuthorityName + "\r\n\r\nSHA-256\r\n" + tlsIdentity.Fingerprint + "\r\n\r\n" +
                    UiText.Get("3. Настройки → Основные → Об этом устройстве → Доверие сертификатам → включите полное доверие только проверенному сертификату PocketPointer. Он добавляет локальный центр доверия; не устанавливайте чужие сертификаты.\r\n\r\n") +
                    UiText.Get("4. Откройте HTTPS-ссылку из главного окна PocketPointer, введите код один раз и запомните устройство. Добавьте новый HTTPS-значок на экран Домой; старый HTTP-значок ведёт только на настройку.\r\n\r\n") +
                    UiText.Get("Не обходите предупреждения Safari. Если предупреждение осталось, проверьте сертификат, доверие и адрес. Удалить доверие можно удалением профиля PocketPointer на iPhone. Закрытый ключ хранится только на этом ПК под защитой Windows; в профиль входит только публичный сертификат.") };
                layout.Controls.Add(details);
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
                var copySetup = Button(UiText.Get("Копировать адрес настройки")); copySetup.Enabled = link != null; copySetup.Click += (s, e) => Clipboard.SetText(setupLink); buttons.Controls.Add(copySetup);
                var export = Button(UiText.Get("Сохранить профиль…")); export.Click += (s, e) => {
                    using (var save = new SaveFileDialog { FileName = "PocketPointer.mobileconfig", Filter = UiText.Get("Профиль сертификата iPhone|*.mobileconfig") })
                        if (save.ShowDialog(dialog) == DialogResult.OK) System.IO.File.WriteAllBytes(save.FileName, tlsIdentity.PublicResponse("/remote.mobileconfig", "").Body);
                }; buttons.Controls.Add(export);
                var close = Button(UiText.Get("Готово")); close.Click += (s, e) => dialog.Close(); buttons.Controls.Add(close);
                layout.Controls.Add(buttons); dialog.Controls.Add(layout); dialog.ShowDialog(this);
            }
        }

        private void ApplyCaption(Form form)
        {
            if (!form.IsHandleCreated) return;
            int enabled = theme.Dark ? 1 : 0;
            int background = ColorTranslator.ToWin32(theme.Canvas);
            int foreground = ColorTranslator.ToWin32(theme.Ink);
            DwmSetWindowAttribute(form.Handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
            DwmSetWindowAttribute(form.Handle, CaptionColor, ref background, sizeof(int));
            DwmSetWindowAttribute(form.Handle, CaptionTextColor, ref foreground, sizeof(int));
        }

        private static TableLayoutPanel Column(Color background, Padding padding)
        {
            var column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, BackColor = background, Padding = padding, Margin = Padding.Empty };
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return column;
        }

        private static FlowLayoutPanel Actions(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = Padding.Empty };
            row.Controls.AddRange(buttons);
            return row;
        }

        private static TableLayoutPanel QuickActions(Button setup, Button devices, Button receive)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Color.Transparent };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            foreach (var button in new[] { setup, devices, receive }) button.Dock = DockStyle.Fill;
            receive.Margin = new Padding(0, 0, 0, 6);
            row.Controls.Add(setup, 0, 0);
            row.Controls.Add(devices, 1, 0);
            row.Controls.Add(receive, 2, 0);
            return row;
        }

        private static TableLayoutPanel FieldAndActions()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        private static void ArrangeFields(TableLayoutPanel grid, Control field, Control actions, bool vertical)
        {
            grid.SuspendLayout();
            grid.Controls.Clear();
            actions.Dock = DockStyle.None;
            actions.Anchor = AnchorStyles.Left;
            actions.Margin = vertical ? new Padding(0, 10, 0, 0) : new Padding(12, 0, 0, 0);
            grid.Controls.Add(field, 0, 0);
            grid.SetColumnSpan(field, vertical ? 2 : 1);
            grid.Controls.Add(actions, vertical ? 0 : 1, vertical ? 1 : 0);
            grid.SetColumnSpan(actions, vertical ? 2 : 1);
            grid.ResumeLayout(true);
        }

        private static Label Label(string text, int size, FontStyle style)
        {
            return new Label { Text = text, AutoSize = true, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, style), Margin = new Padding(0, 3, 0, 7) };
        }

        private Label Caption(string text)
        {
            var label = Label(text, 10, FontStyle.Regular);
            label.ForeColor = theme.Muted;
            label.Margin = new Padding(0, 0, 0, 4);
            return label;
        }

        private void SetStatus(string message, bool error = false)
        {
            status.Text = "●  " + UiText.Get(message);
            statusError = error;
            UpdateStatusColors();
        }

        private void UpdateStatusColors()
        {
            status.BackColor = listening ? theme.StatusFill : theme.Surface;
            status.ForeColor = statusError ? theme.Error : listening ? theme.StatusInk : theme.Muted;
        }

        private Button Button(string text) { var button = new Button { Text = text }; StyleButton(button); return button; }
        private void StyleButton(Button button)
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(12, 7, 12, 7);
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.Margin = new Padding(0, 0, 8, 6);
            DesktopTheme.Round(button, 12);
            ApplyButtonColors(button);
        }

        private void ApplyButtonColors(Button button, bool primary = false)
        {
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? theme.Accent : theme.Button;
            button.FlatAppearance.MouseOverBackColor = primary ? theme.AccentHover : theme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = primary ? theme.Accent : theme.Field;
            button.ForeColor = primary ? Color.White : theme.Ink;
        }
        private void RefreshAddresses()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces().AsEnumerable();
#if STORE
                adapters = StoreRelease.PrivateAdapters(adapters);
#endif
                var ips = adapters
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).Distinct().ToArray();
#if STORE
                string unavailable = UiText.Get("Подключитесь к доверенной частной сети Wi-Fi.");
#else
                string unavailable = UiText.Get("Нет IPv4-адреса.");
#endif
                string text = ips.Length == 0 ? unavailable : string.Join(Environment.NewLine, ips.Select(ip => "https://" + ip + ":" + RemoteServer.DefaultPort + "/").ToArray());
                if (addresses.Text != text) addresses.Text = text;
            }
            catch (NetworkInformationException) { addresses.Text = UiText.Get("Не удалось получить адреса сети."); }
        }
#if STORE
        private void OpenStoreLink(Action open, string message)
        {
            try { open(); }
            catch (System.ComponentModel.Win32Exception) {
                MessageBox.Show(this, UiText.Get(message), "PocketPointer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
#endif
        private void StartServer()
        {
            try { server.Start(RemoteServer.DefaultPort); listening = true; toggle.Text = UiText.Get("Выключить приём"); }
            catch (SocketException ex) { SetStatus(UiText.Get("Не удалось открыть порт: ") + ex.Message, true); toggle.Text = UiText.Get("Повторить запуск"); }
        }
        private void ShowSavedDevices()
        {
            using (var dialog = new Form { Text = UiText.Get("Сохранённые устройства"), ClientSize = new Size(560, 340),
                MinimumSize = new Size(460, 320), StartPosition = FormStartPosition.CenterParent,
                BackColor = BackColor, ForeColor = ForeColor, Font = Font })
            {
                dialog.Icon = appIcon;
                dialog.HandleCreated += (s, e) => ApplyCaption(dialog);
                var list = new ListBox { Dock = DockStyle.Fill, BackColor = theme.Surface, ForeColor = ForeColor,
                    BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
                var help = Label(UiText.Get(server.PairingError ?? "Телефон запоминается после первого подключения по коду.\n«Забыть» отзывает ключ и отключает это устройство."), 10, FontStyle.Regular);
                help.Dock = DockStyle.Top; help.Padding = new Padding(12); help.MaximumSize = new Size(540, 0);
                var remove = Button(UiText.Get("Забыть выбранное устройство"));
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
                        MessageBox.Show(dialog, UiText.Get("Не удалось сохранить изменение. Проверьте доступ к файлу сохранённых устройств."), "PocketPointer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
