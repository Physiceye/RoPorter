using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Rbx2Source
{
    // Settings are remembered between runs in "littlefart" next to the program (or in the user's RoPorter folder, see
    // ContentCache.DataFolder): "name=value" lines, encrypted for the user. On
    // Windows the file is protected for the Windows user (only that user on that PC can read it); on Linux and macOS it is
    // encrypted with a key kept in the user's own config folder, readable only by them. Anywhere else, or if the file is
    // damaged, RoPorter starts with its defaults. Older plain-text files are read and encrypted on the next save.
    public static class SettingsStore
    {
        public static readonly string File = Path.Combine(Export.ContentCache.DataFolder, "littlefart");
        private static readonly byte[] WindowsMagic = Encoding.ASCII.GetBytes("RPS1"), KeyMagic = Encoding.ASCII.GetBytes("RPS2");
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("RoPorter settings");
        private static readonly string KeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "RoPorter", "settings.key");

        // Reads the settings. An older file that holds only a folder path is read as that folder (under folderName).
        public static Dictionary<string, string> Load(ICollection<string> names, string folderName)
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool plain = false;
            try
            {
                if (!System.IO.File.Exists(File))
                    return settings;
                byte[] data = System.IO.File.ReadAllBytes(File);
                string text;
                if (Starts(data, WindowsMagic) && OperatingSystem.IsWindows())
                    text = Encoding.UTF8.GetString(ProtectedData.Unprotect(data.Skip(4).ToArray(), Entropy, DataProtectionScope.CurrentUser));
                else if (Starts(data, KeyMagic))
                    text = Encoding.UTF8.GetString(Decrypt(data.Skip(4).ToArray()));
                else if (Starts(data, WindowsMagic))
                    return settings;
                else
                {
                    plain = true;
                    text = Encoding.UTF8.GetString(data);
                }
                foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    int equals = line.IndexOf('=');
                    string name = equals > 0 ? line.Substring(0, equals).Trim() : "";
                    if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        settings[name] = line.Substring(equals + 1).Trim();
                    else if (line.Trim().Length > 0 && settings.Count == 0)
                        settings[folderName] = line.Trim();
                }
            }
            catch (Exception)
            {
                // Unreadable (another user's or PC's file): the defaults.
            }
            if (plain && settings.Count > 0)
                Save(settings);
            return settings;
        }

        public static void Save(IDictionary<string, string> settings)
        {
            try
            {
                byte[] text = Encoding.UTF8.GetBytes(string.Join("\n", settings.Select(setting => setting.Key + "=" + setting.Value)));
                byte[] data;
                try
                {
                    data = OperatingSystem.IsWindows()
                        ? WindowsMagic.Concat(ProtectedData.Protect(text, Entropy, DataProtectionScope.CurrentUser)).ToArray()
                        : KeyMagic.Concat(Encrypt(text)).ToArray();
                }
                catch (Exception error) when (error is CryptographicException || error is IOException || error is UnauthorizedAccessException ||
                    error is PlatformNotSupportedException)
                {
                    data = text;
                }
                System.IO.File.WriteAllBytes(File, data);
            }
            catch (Exception)
            {
                // A read-only install folder: the settings last until RoPorter closes.
            }
        }

        private static bool Starts(byte[] data, byte[] magic) => data.Length >= magic.Length && data.Take(magic.Length).SequenceEqual(magic);

        // AES-GCM: a 12-byte nonce, the 16-byte tag, then the ciphertext.
        private static byte[] Encrypt(byte[] plain)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
            using (var aes = new AesGcm(Key(true), 16))
                aes.Encrypt(nonce, plain, cipher, tag);
            return nonce.Concat(tag).Concat(cipher).ToArray();
        }

        private static byte[] Decrypt(byte[] data)
        {
            if (data.Length < 28)
                throw new CryptographicException("The settings file is too short.");
            byte[] plain = new byte[data.Length - 28];
            using (var aes = new AesGcm(Key(false), 16))
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return plain;
        }

        // The user's key (32 random bytes), made on first save and readable only by the user.
        private static byte[] Key(bool create)
        {
            if (System.IO.File.Exists(KeyFile))
                return System.IO.File.ReadAllBytes(KeyFile);
            if (!create)
                throw new CryptographicException("There is no settings key for this user.");
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile));
            byte[] key = RandomNumberGenerator.GetBytes(32);
            System.IO.File.WriteAllBytes(KeyFile, key);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
    }
}
