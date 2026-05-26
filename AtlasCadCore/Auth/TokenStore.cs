using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AtlasCadCore.Auth
{
    public class StoredToken
    {
        public string Token { get; set; }
        public string UserId { get; set; }
        public string Email { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public long ExpiresAt { get; set; }

        public bool IsExpired =>
            ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;

        public string DisplayName =>
            !string.IsNullOrEmpty(FirstName) || !string.IsNullOrEmpty(LastName)
                ? ($"{FirstName} {LastName}").Trim()
                : Email;
    }

    public static class TokenStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad");
        private static string TokenFile => Path.Combine(Dir, "token.dat");
        private static readonly byte[] _entropy =
            Encoding.UTF8.GetBytes("AtlasCadPlugin.TokenStore.v1");

        private static StoredToken _cached;

        public static StoredToken Current()
        {
            if (_cached != null) return _cached;
            _cached = Load();
            return _cached;
        }

        public static void Save(StoredToken token)
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            string json = JsonConvert.SerializeObject(token);
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), _entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFile, cipher);
            _cached = token;
        }

        public static void Clear()
        {
            _cached = null;
            if (File.Exists(TokenFile)) File.Delete(TokenFile);
        }

        private static StoredToken Load()
        {
            if (!File.Exists(TokenFile)) return null;
            try
            {
                byte[] cipher = File.ReadAllBytes(TokenFile);
                byte[] plain = ProtectedData.Unprotect(
                    cipher, _entropy, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<StoredToken>(Encoding.UTF8.GetString(plain));
            }
            catch
            {
                return null;
            }
        }
    }
}
