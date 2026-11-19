using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteController
{
    public sealed class PairedDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CreatedUtc { get; set; }
        public override string ToString() { return Name + "  ·  " + Id.Substring(0, 6); }
    }
    public sealed class PairingCredential
    {
        public string Id { get; set; }
        public string Token { get; set; }
    }

    public sealed class PairingStore
    {
        public sealed class Record
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string CreatedUtc { get; set; }
            public string TokenHash { get; set; }
        }
        public sealed class Document
        {
            public int Version { get; set; }
            public List<Record> Devices { get; set; }
        }
        private readonly object gate = new object();
        private readonly string path;
        private static readonly byte[] ProtectedHeader = Encoding.ASCII.GetBytes("REMOTE-PAIRING-DPAPI-1\n");
        private List<Record> devices = new List<Record>();
        public string LoadError { get; private set; }
        public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteController", "devices.json"); } }

        // A null path is an isolated in-memory store for tests and previews.
        public PairingStore(string path)
        {
            this.path = path;
            if (path == null) return;
            try
            {
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 65536) throw new FormatException();
                byte[] stored = File.ReadAllBytes(path);
                bool encrypted = stored.Length >= ProtectedHeader.Length && stored.Take(ProtectedHeader.Length).SequenceEqual(ProtectedHeader);
                byte[] plain = encrypted ? ProtectedData.Unprotect(stored.Skip(ProtectedHeader.Length).ToArray(), null, DataProtectionScope.CurrentUser) : stored;
                Document document;
                try { document = new JavaScriptSerializer().Deserialize<Document>(Encoding.UTF8.GetString(plain).TrimStart('\uFEFF')); }
                finally { Array.Clear(plain, 0, plain.Length); }
                if (document == null || document.Version != 1 || document.Devices == null || document.Devices.Count > 32) throw new FormatException();
                var ids = new HashSet<string>();
                foreach (var device in document.Devices)
                {
                    if (device == null || !IsId(device.Id) || !IsToken(device.TokenHash) || !ids.Add(device.Id) ||
                        device.Name != CleanName(device.Name) || string.IsNullOrEmpty(device.CreatedUtc)) throw new FormatException();
                }
                devices = document.Devices;
                if (!encrypted) Save(devices);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException || ex is InvalidOperationException || ex is CryptographicException)) throw;
                // Preserve the file for recovery and refuse all saved credentials.
                LoadError = "Не удалось прочитать сохранённые устройства. Подключение по коду доступно без запоминания.";
            }
        }
        public PairedDevice[] List()
        {
            lock (gate) return devices.Select(d => new PairedDevice { Id = d.Id, Name = d.Name, CreatedUtc = d.CreatedUtc }).ToArray();
        }
        public PairingCredential Register(string name)
        {
            lock (gate)
            {
                if (LoadError != null) throw new IOException(LoadError);
                if (devices.Count >= 32) throw new InvalidOperationException("Сохранено 32 устройства. Удалите ненужные в окне Windows.");
                byte[] bytes = new byte[32];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                var credential = new PairingCredential { Id = Guid.NewGuid().ToString("N"), Token = Hex(bytes) };
                var updated = new List<Record>(devices) { new Record { Id = credential.Id, Name = CleanName(name),
                    CreatedUtc = DateTime.UtcNow.ToString("o"), TokenHash = Hash(credential.Token) } };
                Save(updated);
                devices = updated;
                return credential;
            }
        }
        public bool Authenticate(string id, string token)
        {
            if (!IsId(id) || !IsToken(token)) return false;
            lock (gate)
            {
                if (LoadError != null) return false;
                var device = devices.FirstOrDefault(d => d.Id == id);
                if (device == null) return false;
                string hash = Hash(token);
                int difference = 0;
                for (int i = 0; i < hash.Length; i++) difference |= hash[i] ^ device.TokenHash[i];
                return difference == 0;
            }
        }
        public bool Forget(string id)
        {
            lock (gate)
            {
                if (LoadError != null) throw new IOException(LoadError);
                var updated = devices.Where(d => d.Id != id).ToList();
                if (updated.Count == devices.Count) return false;
                Save(updated);
                devices = updated;
                return true;
            }
        }
        private void Save(List<Record> updated)
        {
            if (path == null) return;
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".devices-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Document { Version = 1, Devices = updated }));
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser); }
                finally { Array.Clear(data, 0, data.Length); }
                data = ProtectedHeader.Concat(encrypted).ToArray();
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data, 0, data.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string CleanName(string value)
        {
            string name = new string((value ?? "").Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
            return name.Length == 0 ? "iPhone / Safari" : name;
        }
        private static bool IsId(string value) { return value != null && Regex.IsMatch(value, "\\A[0-9a-f]{32}\\z"); }
        private static bool IsToken(string value) { return value != null && Regex.IsMatch(value, "\\A[0-9A-F]{64}\\z"); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
        private static string Hash(string token) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.ASCII.GetBytes(token))); }
    }
}
