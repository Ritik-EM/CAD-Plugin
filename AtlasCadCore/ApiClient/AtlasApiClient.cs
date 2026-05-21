using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AtlasCadCore.Auth;
using Newtonsoft.Json;

namespace AtlasCadCore.ApiClient
{
    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    /// <summary>
    /// Single HTTP client shared by all three CAD plugins. Attaches the
    /// JWT from TokenStore as Authorization: Bearer on every call.
    /// </summary>
    public class AtlasApiClient
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public string BaseUrl { get; private set; }

        public AtlasApiClient(string baseUrl)
        {
            BaseUrl = baseUrl.TrimEnd('/');
        }

        internal HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
            var token = TokenStore.Current();
            if (token != null && !string.IsNullOrEmpty(token.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            return req;
        }

        internal async Task<string> SendAsync(HttpRequestMessage req, string operation)
        {
            string url = req.RequestUri?.ToString() ?? "(null)";
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req);
            }
            catch (HttpRequestException ex)
            {
                // HttpClient surfaces all socket/TLS/DNS/proxy failures as a
                // single HttpRequestException with a generic message. The
                // actual cause is in InnerException — expose it so the user
                // (and us, on the receiving end of a screenshot) can tell
                // connection-refused apart from TLS apart from DNS.
                var details = new System.Text.StringBuilder();
                details.AppendLine($"{operation} — transport error for {req.Method} {url}");
                details.AppendLine($"  HttpClient: {ex.Message}");
                Exception inner = ex.InnerException;
                while (inner != null)
                {
                    details.AppendLine($"  ↳ {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }
                throw new Exception(details.ToString(), ex);
            }
            string body = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedException("Your session has expired. Please sign in again.");

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{operation} failed: {(int)resp.StatusCode} {body}");

            return body;
        }

        // ---- Endpoints (P7 surface; assembly-flow endpoints removed in P7.0) ----

        public async Task<PluginVersionDto> LatestVersionAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/cad/version/latest");
            string body = await SendAsync(req, "Version check");
            return JsonConvert.DeserializeObject<ApiEnvelope<PluginVersionDto>>(body).data;
        }

        public async Task<string> PingAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/cad/ping");
            return await SendAsync(req, "Ping");
        }

        // ---- Browse Part Master Library ----

        public async Task<PaginatedDto<PartMasterDocumentDto>> ListPartMasterAsync(
            string releaseType, string search, int page, int limit)
        {
            var qs = new List<string>
            {
                $"page={page}",
                $"limit={limit}",
            };
            if (!string.IsNullOrEmpty(releaseType))
                qs.Add($"release_type={Uri.EscapeDataString(releaseType)}");
            if (!string.IsNullOrEmpty(search))
                qs.Add($"search={Uri.EscapeDataString(search)}");

            var req = NewRequest(HttpMethod.Get, "/api/v1/part-master/part-number?" + string.Join("&", qs));
            string body = await SendAsync(req, "List part-master");
            return JsonConvert.DeserializeObject<ApiEnvelope<PaginatedDto<PartMasterDocumentDto>>>(body).data;
        }

        public async Task<MyCheckoutsDto> MyCheckoutsAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/cad/part-master/my-checkouts");
            string body = await SendAsync(req, "My checkouts");
            return JsonConvert.DeserializeObject<ApiEnvelope<MyCheckoutsDto>>(body).data;
        }

        public async Task<CheckoutResultDto> CheckoutPartMasterAsync(string partNumber)
        {
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/part-master/{Uri.EscapeDataString(partNumber)}/checkout");
            string body = await SendAsync(req, "Checkout");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckoutResultDto>>(body).data;
        }

        public async Task CancelCheckoutPartMasterAsync(string partNumber)
        {
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/part-master/{Uri.EscapeDataString(partNumber)}/cancel-checkout");
            await SendAsync(req, "Cancel-checkout");
        }

        /// <summary>
        /// First-time upload. `tree` is a flat list of UploadTreeEntry payload
        /// objects (anonymous types are fine — they get JSON-serialised) and
        /// `filePaths` is the set of local files referenced by the tree.
        ///
        /// `releaseNewRevision` (default false) attaches each uploaded file
        /// to the existing active revision of its part_number. When true,
        /// the backend mints a fresh revision per entry before attaching —
        /// OTP must be supplied in that case.
        /// </summary>
        public async Task<UploadResultDto> UploadPartMasterAsync(
            IEnumerable<object> tree, IEnumerable<string> filePaths,
            bool releaseNewRevision = false, string otp = null)
        {
            using (var content = BuildTreeMultipart(tree, filePaths))
            {
                if (releaseNewRevision)
                {
                    content.Add(new StringContent("true"), "release_new_revision");
                    if (!string.IsNullOrEmpty(otp))
                        content.Add(new StringContent(otp), "otp");
                }
                var req = NewRequest(HttpMethod.Post, "/api/v1/cad/part-master/upload");
                req.Content = content;
                string body = await SendAsync(req, "Upload to part-master");
                return JsonConvert.DeserializeObject<ApiEnvelope<UploadResultDto>>(body).data;
            }
        }

        public async Task<CheckinPreviewResultDto> CheckinPreviewAsync(
            string rootPartNumber, IEnumerable<object> tree, string releaseType, List<string> changed)
        {
            var req = NewRequest(HttpMethod.Post,
                $"/api/v1/cad/part-master/{Uri.EscapeDataString(rootPartNumber)}/checkin/preview");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { tree, release_type = releaseType, changed = changed ?? new List<string>() }),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Checkin preview");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckinPreviewResultDto>>(body).data;
        }

        public async Task<CheckinResultDto> CheckinAsync(
            string rootPartNumber, IEnumerable<object> tree, string releaseType,
            List<string> changed, string comment, string otp, IEnumerable<string> filePaths)
        {
            using (var content = BuildTreeMultipart(tree, filePaths))
            {
                content.Add(new StringContent(releaseType), "release_type");
                content.Add(new StringContent(otp ?? ""), "otp");
                content.Add(new StringContent(JsonConvert.SerializeObject(changed ?? new List<string>())), "changed_json");
                content.Add(new StringContent(comment ?? ""), "comment");
                var req = NewRequest(HttpMethod.Post,
                    $"/api/v1/cad/part-master/{Uri.EscapeDataString(rootPartNumber)}/checkin");
                req.Content = content;
                string body = await SendAsync(req, "Checkin");
                return JsonConvert.DeserializeObject<ApiEnvelope<CheckinResultDto>>(body).data;
            }
        }

        /// <summary>
        /// Trigger backend to generate an OTP for the release-revision action
        /// and email it to the user. Used by the Check In flow — user enters
        /// the emailed code to authorise the revision batch.
        /// </summary>
        public async Task RequestReleaseRevisionOtpAsync()
        {
            var req = NewRequest(HttpMethod.Post, "/api/v1/auth/otp");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new
                {
                    action = "RELEASE_REVISION",
                    resource = "/part-master/part-number/release-revision",
                    check_permission_for_otp_generation = false,
                }),
                System.Text.Encoding.UTF8, "application/json");
            await SendAsync(req, "Generate OTP");
        }

        public async Task<CreateBatchResultDto> CreateBatchAsync(List<CreateBatchEntryDto> entries)
        {
            var req = NewRequest(HttpMethod.Post, "/api/v1/cad/part-master/create-batch");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { entries }),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Create-batch");
            return JsonConvert.DeserializeObject<ApiEnvelope<CreateBatchResultDto>>(body).data;
        }

        /// <summary>
        /// Multipart helper shared by upload + checkin. Adds `tree_json` as
        /// the serialised tree and one `files` part per referenced local file.
        /// Caller is responsible for any extra form fields (release_type, etc.)
        /// which they can add to the returned content before sending.
        /// </summary>
        internal MultipartFormDataContent BuildTreeMultipart(
            IEnumerable<object> tree, IEnumerable<string> filePaths)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(JsonConvert.SerializeObject(tree)), "tree_json");

            // De-dup by filename — the backend matches files-to-entries by
            // bare filename, so uploading the same one twice would shadow.
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                string filename = Path.GetFileName(path);
                if (!added.Add(filename)) continue;
                var stream = OpenSharedRead(path);
                content.Add(new StreamContent(stream), "files", filename);
            }
            return content;
        }

        public async Task<string> GetS3DownloadUrlAsync(string s3Key)
        {
            var req = NewRequest(HttpMethod.Get,
                "/api/v1/s3/download/presigned?key=" + Uri.EscapeDataString(s3Key));
            string body = await SendAsync(req, "S3 presign");
            return JsonConvert.DeserializeObject<ApiEnvelope<S3PresignedDownloadDto>>(body).data.url;
        }

        public async Task<PartLookupResult> LookupPartNumbersAsync(List<string> partNumbers)
        {
            var req = NewRequest(HttpMethod.Post, "/api/v1/cad/part-master/lookup");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { part_numbers = partNumbers }),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Part lookup");
            return JsonConvert.DeserializeObject<ApiEnvelope<PartLookupResult>>(body).data;
        }

        public async Task DownloadFileAsync(string presignedUrl, string targetPath)
        {
            // Presigned S3 URLs must NOT carry Authorization. Use a clean request.
            Exception last = null;
            for (int i = 0; i < 3; i++)
            {
                try
                {
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
                    return;
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                    if (i < 2) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            throw last;
        }

        internal static Stream OpenSharedRead(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                string tempPath = Path.Combine(Path.GetTempPath(),
                    "AtlasUpload_" + Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path));
                File.Copy(path, tempPath, overwrite: true);
                return new FileStream(tempPath, FileMode.Open, FileAccess.Read);
            }
        }
    }
}
