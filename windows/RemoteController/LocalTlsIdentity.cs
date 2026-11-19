using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteController
{
    public sealed class LocalTlsIdentity : IDisposable
    {
        private readonly object gate = new object();
        private readonly string directory;
        private readonly X509Certificate2 authority;
        private X509Certificate2 leaf;
        private string addressKey;
        // Retain replaced certificates until shutdown: existing TLS handshakes may still use them.
        private readonly List<X509Certificate2> retired = new List<X509Certificate2>();
        public static string DefaultDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteController", "tls"); } }
        public string Fingerprint { get; private set; }
        public string AuthorityName { get { return authority.GetNameInfo(X509NameType.SimpleName, false); } }
        public byte[] PublicCertificate { get { return authority.Export(X509ContentType.Cert); } }
        public LocalTlsIdentity(string directory)
        {
            this.directory = directory; Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "authority.dpapi");
            if (File.Exists(path)) authority = Load(path);
            else {
                using (var key = new RSACng(3072)) {
                    var request = new CertificateRequest("CN=Remote Local CA " + Guid.NewGuid().ToString("N").Substring(0, 10), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
                    using (var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5))) {
                        Save(path, generated); authority = Load(path);
                    }
                }
            }
            if (!authority.HasPrivateKey || authority.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7)) throw new CryptographicException("Локальный сертификат Remote повреждён или истёк. Необходимо восстановить сертификат; незашифрованный режим отключён.");
            using (var sha = SHA256.Create()) Fingerprint = BitConverter.ToString(sha.ComputeHash(authority.RawData)).Replace('-', ':');
            GetCertificate();
        }
        private static X509Certificate2 Load(string path)
        {
            byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { return new X509Certificate2(data, (string)null, X509KeyStorageFlags.UserKeySet); }
            finally { Array.Clear(data, 0, data.Length); }
        }
        private static void Save(string path, X509Certificate2 certificate)
        {
            byte[] data = certificate.Export(X509ContentType.Pfx);
            byte[] protectedData;
            try { protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(data, 0, data.Length); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllBytes(temporary, protectedData);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static IPAddress[] Addresses()
        {
            var addresses = new List<IPAddress> { IPAddress.Loopback };
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                if (network.OperationalStatus == OperationalStatus.Up)
                    addresses.AddRange(network.GetIPProperties().UnicastAddresses.Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork));
            return addresses.Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
        }
        public X509Certificate2 GetCertificate()
        {
            lock (gate) {
                var addresses = Addresses();
                string current = string.Join(",", addresses.Select(a => a.ToString()));
                if (leaf != null && addressKey == current && leaf.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)) return leaf;
                string path = Path.Combine(directory, "server.dpapi"), namesPath = Path.Combine(directory, "addresses.txt");
                if (leaf == null && File.Exists(path) && File.Exists(namesPath) && File.ReadAllText(namesPath) == current) {
                    var saved = Load(path);
                    if (saved.HasPrivateKey && saved.Issuer == authority.Subject && saved.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)) { leaf = saved; addressKey = current; return leaf; }
                    saved.Dispose();
                }
                using (var key = new RSACng(2048)) {
                    var request = new CertificateRequest("CN=Remote local connection", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var names = new SubjectAlternativeNameBuilder(); foreach (var address in addresses) names.AddIpAddress(address);
                    request.CertificateExtensions.Add(names.Build());
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    byte[] serial = new byte[16]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(serial); serial[0] = (byte)((serial[0] & 127) | 1);
                    DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(90), rootExpiry = new DateTimeOffset(authority.NotAfter.ToUniversalTime()).AddMinutes(-1);
                    if (expiry > rootExpiry) expiry = rootExpiry;
                    using (var signed = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), expiry, serial))
                    using (var withKey = signed.CopyWithPrivateKey(key)) Save(path, withKey);
                }
                var replacement = Load(path);
                File.WriteAllText(namesPath, current, Encoding.ASCII);
                if (leaf != null) retired.Add(leaf);
                leaf = replacement; addressKey = current; return leaf;
            }
        }
        public SetupResponse PublicResponse(string route, string secureOrigin)
        {
            if (route == "/setup.css") return new SetupResponse("text/css; charset=utf-8", Encoding.UTF8.GetBytes("html{font:16px -apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;background:#0d121a;color:#edf2f7}body{margin:0;padding:24px 20px}main{max-width:680px;margin:auto}h1{font-size:26px}p,li{line-height:1.6}li{margin:14px 0}ol{padding-left:24px}a{color:#87edc6}code{overflow-wrap:anywhere;font-size:13px}strong{overflow-wrap:anywhere}"));
            if (route == "/remote-ca.cer") return new SetupResponse("application/x-x509-ca-cert", PublicCertificate);
            if (route == "/remote.mobileconfig") return new SetupResponse("application/x-apple-aspen-config", Encoding.UTF8.GetBytes(Profile()));
            if (route != "/" && route != "/index.html" && route != "/setup") return null;
            string html = "<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>Remote — защищённое подключение</title><link rel=stylesheet href=/setup.css>" +
                "<body><main><h1>Защищённое подключение Remote</h1><p>Выполните настройку один раз на этом iPhone. Эта страница содержит только инструкцию и публичный сертификат. Не вводите здесь код или текст для ноутбука.</p>" +
                "<ol><li>Откройте «Защищённое подключение» в окне Remote на ноутбуке. Сверьте имя и полный SHA-256 отпечаток сертификата с данными ниже. При несовпадении остановитесь.</li>" +
                "<li><a href=/remote.mobileconfig>Скачать профиль сертификата Remote</a>. В сведениях сертификата профиля сверьте весь отпечаток SHA-256 с окном Remote в Windows. Затем установите профиль: Настройки → Основные → VPN и управление устройством → загруженный профиль.</li>" +
                "<li>Откройте Настройки → Основные → Об этом устройстве → Доверие сертификатам. Включите полное доверие только сертификату Remote с проверенным именем.</li>" +
                "<li><a href=\"" + WebUtility.HtmlEncode(secureOrigin + "/") + "\">Открыть защищённый Remote по HTTPS</a>. Один раз введите код подключения и включите «Запомнить устройство».</li></ol>" +
                "<p>Имя: <strong>" + WebUtility.HtmlEncode(AuthorityName) + "</strong></p><p>SHA-256: <code>" + Fingerprint + "</code></p>" +
                "<p>Сертификат добавляет локальный центр доверия. Устанавливайте его только для своего ПК после сверки. Не обходите предупреждения Safari: если они остались, проверьте установку, доверие и адрес.</p>" +
                "<p>Старые HTTP-ссылки открывают только настройку. Для значка на экране Домой добавьте новую HTTPS-страницу через Safari → Поделиться → На экран Домой.</p>" +
                "<p>Удалить доверие можно, удалив профиль Remote в настройках управления устройством.</p></main></body></html>";
            return new SetupResponse("text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }
        private string Profile()
        {
            string identifier = "local.remotecontroller." + Fingerprint.Replace(":", "").ToLowerInvariant();
            string name = SecurityElement.Escape(AuthorityName);
            string uuid = Guid.NewGuid().ToString().ToUpperInvariant();
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict>" +
                "<key>PayloadType</key><string>Configuration</string><key>PayloadVersion</key><integer>1</integer><key>PayloadIdentifier</key><string>" + identifier + "</string>" +
                "<key>PayloadUUID</key><string>" + uuid + "</string><key>PayloadDisplayName</key><string>" + name + "</string><key>PayloadRemovalDisallowed</key><false/>" +
                "<key>PayloadDescription</key><string>Сертификат для локального HTTPS-подключения к вашему ПК Remote. Сверьте отпечаток в Windows перед установкой.</string>" +
                "<key>PayloadContent</key><array><dict><key>PayloadType</key><string>com.apple.security.root</string><key>PayloadVersion</key><integer>1</integer>" +
                "<key>PayloadIdentifier</key><string>" + identifier + ".certificate</string><key>PayloadUUID</key><string>" + Guid.NewGuid().ToString().ToUpperInvariant() + "</string>" +
                "<key>PayloadDisplayName</key><string>" + name + "</string><key>PayloadCertificateFileName</key><string>Remote-CA.cer</string><key>PayloadContent</key><data>" + Convert.ToBase64String(PublicCertificate) + "</data></dict></array></dict></plist>";
        }
        public void Dispose() { lock (gate) { if (leaf != null) leaf.Dispose(); foreach (var cert in retired) cert.Dispose(); authority.Dispose(); } }
    }
}
