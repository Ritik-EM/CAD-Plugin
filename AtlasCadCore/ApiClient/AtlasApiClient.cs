using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AtlasCadCore.Auth;
using AtlasCadCore.Utility;
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
        private static readonly HttpClient _http = CreateHttpClient();
        // Separate client for direct-to-S3 PUTs (the presigned-upload flow).
        // S3 doesn't need Bearer/X-Atlas-Plugin and adding them might mismatch
        // the presigned signature; isolate so the atlas-api defaults don't
        // leak into the S3 request. Longer timeout for big multi-MB uploads.
        private static readonly HttpClient _s3Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            string ver = PluginVersion.Current?.ToString(3) ?? "dev";
            // CloudFront / AWS WAF in front of prod atlas-api blocks requests
            // with no User-Agent (the .NET HttpClient default) as bot traffic.
            // A descriptive UA both passes the basic bot rules AND lets
            // backend access logs attribute calls to a specific plugin build.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"AtlasCadPlugin/{ver} (.NET 4.8; SolidWorks)");
            // X-Atlas-Plugin is the *canonical* signal for "this came from the
            // CAD plugin, not a browser". Cleaner WAF allow-list than UA
            // substring matching — AWS team can add a single rule
            //   `header X-Atlas-Plugin starts-with "AtlasCadPlugin/" → ALLOW`
            // that covers every endpoint. File bytes themselves now bypass
            // CloudFront entirely via direct-to-S3 PUTs (see PresignUploadAsync
            // + PutFileToS3Async) so WAF only ever sees small JSON requests.
            http.DefaultRequestHeaders.Add("X-Atlas-Plugin", $"AtlasCadPlugin/{ver}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return http;
        }

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
        /// First-time upload using the presigned-PUT flow.
        ///   1. Ask atlas-api for a session_id + one presigned URL per distinct filename.
        ///   2. PUT each file's bytes directly to S3 (bypasses CloudFront).
        ///   3. POST /cad/part-master/upload with session_id + tree (tiny JSON body).
        /// File bytes never traverse atlas-api or CloudFront, so WAF only sees
        /// small JSON requests it has no reason to block.
        /// </summary>
        public async Task<UploadResultDto> UploadPartMasterAsync(
            IEnumerable<object> tree, IEnumerable<string> filePaths,
            bool releaseNewRevision = false, string otp = null)
        {
            var pathList = filePaths?.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                                    .ToList() ?? new List<string>();
            string sessionId = await StageFilesAsync(pathList, "Upload to part-master");

            var payload = new
            {
                session_id = sessionId,
                tree = tree,
                release_new_revision = releaseNewRevision,
                otp = otp ?? "",
            };
            var req = NewRequest(HttpMethod.Post, "/api/v1/cad/part-master/upload");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(payload),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Upload to part-master");
            return JsonConvert.DeserializeObject<ApiEnvelope<UploadResultDto>>(body).data;
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
            var pathList = filePaths?.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                                    .ToList() ?? new List<string>();
            string sessionId = await StageFilesAsync(pathList, "Checkin");

            var payload = new
            {
                session_id = sessionId,
                tree = tree,
                release_type = releaseType,
                changed = changed ?? new List<string>(),
                comment = comment ?? "",
                otp = otp ?? "",
            };
            var req = NewRequest(HttpMethod.Post,
                $"/api/v1/cad/part-master/{Uri.EscapeDataString(rootPartNumber)}/checkin");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(payload),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Checkin");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckinResultDto>>(body).data;
        }

        /// <summary>
        /// Presign + PUT-to-S3 for every distinct file in filePaths.
        /// Returns the session_id the caller must pass to the finalize endpoint.
        /// </summary>
        public async Task<string> StageFilesAsync(IEnumerable<string> filePaths, string operation)
        {
            // De-dup by filename — the backend keys staging by filename within
            // a session, so uploading the same one twice would just overwrite.
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                string filename = Path.GetFileName(path);
                if (!byName.ContainsKey(filename)) byName[filename] = path;
            }
            var presign = await PresignUploadAsync(byName.Keys.ToList());
            if (presign?.uploads == null)
                throw new InvalidOperationException($"{operation}: presign returned no upload slots");

            // Upload all files in parallel — each PUT goes direct to S3 so
            // there's no atlas-api bottleneck to serialise against.
            var puts = presign.uploads
                .Where(u => !string.IsNullOrEmpty(u.presigned_url) && byName.ContainsKey(u.filename))
                .Select(u => PutFileToS3Async(u.presigned_url, byName[u.filename]))
                .ToList();
            await Task.WhenAll(puts);
            return presign.session_id;
        }

        public async Task<PresignUploadResultDto> PresignUploadAsync(List<string> filenames)
        {
            var req = NewRequest(HttpMethod.Post, "/api/v1/cad/files/presign-upload");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { filenames }),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Presign upload");
            return JsonConvert.DeserializeObject<ApiEnvelope<PresignUploadResultDto>>(body).data;
        }

        /// <summary>
        /// PUT a single file's bytes directly to a presigned S3 URL. Uses
        /// the dedicated _s3Http client so no atlas-api headers (Bearer,
        /// X-Atlas-Plugin) leak into the request — AWS signs only specific
        /// headers and unrelated ones can mismatch the signature.
        /// </summary>
        public async Task PutFileToS3Async(string presignedUrl, string filePath)
        {
            using (var stream = OpenSharedRead(filePath))
            using (var content = new StreamContent(stream))
            using (var req = new HttpRequestMessage(HttpMethod.Put, presignedUrl) { Content = content })
            {
                var resp = await _s3Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"S3 PUT failed ({(int)resp.StatusCode}) for {Path.GetFileName(filePath)}: {errBody}");
                }
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

        // BuildTreeMultipart removed — the upload + checkin flows no longer
        // send multipart bodies through atlas-api. Bytes go direct to S3 via
        // presigned PUTs and the finalize endpoints take JSON. See
        // StageFilesAsync / PutFileToS3Async / PresignUploadAsync.

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
