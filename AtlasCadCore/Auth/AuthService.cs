using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AtlasCadCore.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AtlasCadCore.Auth
{
    public class AuthException : Exception
    {
        public AuthException(string message) : base(message) { }
    }

    /// <summary>
    /// Exchanges username+password for a JWT against Euler's octopus auth service.
    /// CAD-agnostic — same instance used by SolidWorks / CATIA / NX plugins.
    /// </summary>
    public class AuthService
    {
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // CloudFront / AWS WAF blocks requests with no User-Agent. Set
            // one matching what AtlasApiClient sends so the auth call passes
            // the edge in prod (it was returning a 403 HTML page from
            // CloudFront otherwise — looked like an atlas-api failure).
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"AtlasCadPlugin/{PluginVersion.Current?.ToString(3) ?? "dev"} (.NET 4.8; SolidWorks)");
            return http;
        }
        private readonly string _tokenUrl;

        public AuthService(string octopusBaseUrl)
        {
            _tokenUrl = octopusBaseUrl.TrimEnd('/') + "/api/auth/accounts/token/";
        }

        public async Task<StoredToken> LoginAsync(string user, string plainPassword)
        {
            string hashed = Sha256Hex(plainPassword);
            string body = JsonConvert.SerializeObject(new { user = user, password = hashed });
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try { resp = await _http.PostAsync(_tokenUrl, content); }
            catch (Exception ex) { throw new AuthException("Cannot reach auth server: " + ex.Message); }

            string respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new AuthException(
                    $"Login failed ({(int)resp.StatusCode}): {ExtractMessage(respBody) ?? "check username/password"}");

            JObject envelope = JObject.Parse(respBody);
            if (envelope.Value<bool?>("success") != true)
                throw new AuthException("Login failed: " + envelope.Value<string>("message"));

            string token = envelope["data"]?.Value<string>("token");
            if (string.IsNullOrEmpty(token))
                throw new AuthException("Auth response missing token");

            StoredToken stored = DecodePayload(token);
            TokenStore.Save(stored);
            return stored;
        }

        private static StoredToken DecodePayload(string jwt)
        {
            string[] parts = jwt.Split('.');
            if (parts.Length != 3) throw new AuthException("Malformed JWT");

            JObject p = JObject.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            return new StoredToken
            {
                Token = jwt,
                UserId = p.Value<string>("user_id"),
                Email = p.Value<string>("email"),
                FirstName = p.Value<string>("first_name"),
                LastName = p.Value<string>("last_name"),
                ExpiresAt = p.Value<long?>("exp") ?? 0,
            };
        }

        private static byte[] Base64UrlDecode(string s)
        {
            string padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ExtractMessage(string body)
        {
            try { return JObject.Parse(body).Value<string>("message"); }
            catch { return null; }
        }
    }
}
