using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AtlasCadPlugin.Auth;
using Newtonsoft.Json;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Raised when atlas-api rejects the request with HTTP 401. The caller
    /// (AtlasAddin.Run) catches this and re-prompts the user to sign in.
    /// </summary>
    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    public class AssemblyDto
    {
        public string id;
        public string name;
        public string root_filename;
        public int current_version;
        public string locked_by;
        public string locked_at;
        public string created_by;
        public string created_at;
    }

    public class CheckoutFileDto
    {
        public string relative_path;
        public string filename;
        public bool is_root;
        public long size_bytes;
        public string download_url;
    }

    public class CheckoutResultDto
    {
        public string assembly_id;
        public string name;
        public int version_number;
        public string locked_by;
        public string locked_at;
        public List<CheckoutFileDto> files;
    }

    public class ApiEnvelope<T>
    {
        public bool success;
        public string message;
        public int resp_code;
        public T data;
    }

    /// <summary>
    /// Wraps all HTTP calls to the atlas-api /api/v1/cad/* endpoints.
    /// Attaches the JWT from TokenStore as Authorization: Bearer on every
    /// request. On 401 throws UnauthorizedException so the caller can
    /// prompt the user to sign in again.
    /// </summary>
    public class AtlasApiClient
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public string BaseUrl { get; private set; }

        public AtlasApiClient(string baseUrl)
        {
            BaseUrl = baseUrl.TrimEnd('/');
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
            var token = TokenStore.Current();
            if (token != null && !string.IsNullOrEmpty(token.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            return req;
        }

        private async Task<string> SendAsync(HttpRequestMessage req, string operation)
        {
            HttpResponseMessage resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedException("Your session has expired. Please sign in again.");

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{operation} failed: {(int)resp.StatusCode} {body}");

            return body;
        }

        public async Task<string> PingAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/cad/ping");
            return await SendAsync(req, "Ping");
        }

        public async Task<UploadResultDto> UploadAssemblyAsync(string name, List<AssemblyFileRef> tree)
        {
            using (var content = BuildMultipart(name, tree, comment: null))
            {
                var req = NewRequest(HttpMethod.Post, "/api/v1/cad/assembly/upload");
                req.Content = content;
                string body = await SendAsync(req, "Upload");
                return JsonConvert.DeserializeObject<ApiEnvelope<UploadResultDto>>(body).data;
            }
        }

        public async Task<List<AssemblyDto>> ListAssembliesAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/cad/assembly");
            string body = await SendAsync(req, "List");
            return JsonConvert.DeserializeObject<ApiEnvelope<List<AssemblyDto>>>(body).data
                ?? new List<AssemblyDto>();
        }

        public async Task<CheckoutResultDto> CheckoutAsync(string assemblyId)
        {
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/assembly/{assemblyId}/checkout");
            string body = await SendAsync(req, "Checkout");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckoutResultDto>>(body).data;
        }

        public async Task<UploadResultDto> CheckinAsync(string assemblyId, List<AssemblyFileRef> tree, string comment)
        {
            using (var content = BuildMultipart(name: null, tree: tree, comment: comment))
            {
                var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/assembly/{assemblyId}/checkin");
                req.Content = content;
                string body = await SendAsync(req, "Check-in");
                return JsonConvert.DeserializeObject<ApiEnvelope<UploadResultDto>>(body).data;
            }
        }

        public async Task CancelCheckoutAsync(string assemblyId)
        {
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/assembly/{assemblyId}/cancel-checkout");
            await SendAsync(req, "Cancel-checkout");
        }

        public async Task DownloadFileAsync(string presignedUrl, string targetPath)
        {
            // Presigned S3 URLs are credential-embedded — they MUST NOT carry
            // our Authorization header (S3 rejects requests with extra auth headers).
            // Use a clean HttpRequestMessage with no Bearer token.
            var req = new HttpRequestMessage(HttpMethod.Get, presignedUrl);
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                using (var inStream = await resp.Content.ReadAsStreamAsync())
                using (var outStream = File.Create(targetPath))
                {
                    await inStream.CopyToAsync(outStream);
                }
            }
        }

        private MultipartFormDataContent BuildMultipart(string name, List<AssemblyFileRef> tree, string comment)
        {
            var content = new MultipartFormDataContent();

            if (name != null)
                content.Add(new StringContent(name), "name");

            // tree_json carries per-file metadata so the backend can rebuild
            // the folder structure and identify the root file.
            var treePayload = new
            {
                files = tree.ConvertAll(t => new {
                    relative_path = t.RelativePath.Replace('\\', '/'),
                    filename = t.Filename,
                    is_root = t.IsRoot,
                })
            };
            content.Add(new StringContent(JsonConvert.SerializeObject(treePayload)), "tree_json");

            if (comment != null)
                content.Add(new StringContent(comment), "comment");

            foreach (var f in tree)
            {
                // SolidWorks holds the active assembly file open. Default
                // File.OpenRead requests exclusive read which fails with
                // "being used by another process". FileShare.ReadWrite tells
                // Windows we're OK reading while SolidWorks has it open.
                var stream = OpenSharedRead(f.FullPath);
                var fileContent = new StreamContent(stream);
                content.Add(fileContent, "files", f.Filename);
            }

            return content;
        }

        private static Stream OpenSharedRead(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                // Last-resort fallback: copy via CopyFileEx into a temp file
                // we control fully, then read that. Temp file leaks intentionally
                // (Windows TEMP gets cleaned periodically).
                string tempPath = Path.Combine(
                    Path.GetTempPath(),
                    "AtlasUpload_" + Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path)
                );
                File.Copy(path, tempPath, overwrite: true);
                return new FileStream(tempPath, FileMode.Open, FileAccess.Read);
            }
        }
    }

    public class UploadResultDto
    {
        public string assembly_id;
        public int version_number;
        public string name;
    }
}
