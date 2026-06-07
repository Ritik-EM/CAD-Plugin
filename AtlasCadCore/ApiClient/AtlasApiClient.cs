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
using Newtonsoft.Json.Linq;

namespace AtlasCadCore.ApiClient
{
    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    public class AtlasApiClient
    {
        private static readonly HttpClient _http = CreateHttpClient();
        private static readonly HttpClient _s3Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            string ver = PluginVersion.Current?.ToString(3) ?? "dev";
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"AtlasCadPlugin/{ver} (.NET 4.8; SolidWorks)");
            
            http.DefaultRequestHeaders.Add("X-Atlas-Plugin", $"AtlasCadPlugin/{ver}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return http;
        }

        public string BaseUrl { get; private set; }

        // Which CAD app this client serves — sent as X-Atlas-Cad-Source so the
        // backend can stamp the cad_transactions audit row (CATIA/SOLIDWORKS/NX).
        private readonly string _source;

        public AtlasApiClient(string baseUrl, string source = null)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            _source = source;
        }

        internal HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
            var token = TokenStore.Current();
            if (token != null && !string.IsNullOrEmpty(token.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            if (!string.IsNullOrEmpty(_source))
                req.Headers.Add("X-Atlas-Cad-Source", _source);
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

        public async Task<string> StageFilesAsync(IEnumerable<string> filePaths, string operation)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                string filename = Path.GetFileName(path);
                if (!byName.ContainsKey(filename)) byName[filename] = path;
            }
            LogUpload($"=== STAGE ({operation}): {byName.Count} unique file(s) to S3 ===");
            foreach (var kv in byName)
            {
                long size = -1; try { size = new FileInfo(kv.Value).Length; } catch { }
                LogUpload($"  stage '{kv.Key}' size={size}B");
            }

            var presign = await PresignUploadAsync(byName.Keys.ToList());
            if (presign?.uploads == null)
                throw new InvalidOperationException($"{operation}: presign returned no upload slots");
            LogUpload($"  presign session={presign.session_id} slots={presign.uploads.Count}");

            var puts = presign.uploads
                .Where(u => !string.IsNullOrEmpty(u.presigned_url) && byName.ContainsKey(u.filename))
                .Select(async u =>
                {
                    try { await PutFileToS3Async(u.presigned_url, byName[u.filename]); LogUpload($"  PUT ok  '{u.filename}' -> {u.s3_key}"); }
                    catch (Exception ex) { LogUpload($"  PUT FAIL '{u.filename}': {ex.Message}"); throw; }
                })
                .ToList();
            await Task.WhenAll(puts);
            LogUpload($"  staged {puts.Count} file(s) under session {presign.session_id}");
            return presign.session_id;
        }

        // Mirrors UploadToPartMasterForm.LogUpload — same %AppData%\AtlasCad\upload.log
        // so the staging (S3 PUT) detail sits inline with the upload payload/result.
        private static void LogUpload(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "upload.log"),
                    $"--- {DateTime.Now:O} ApiClient.{line}\n");
            }
            catch { /* logging must never break the upload */ }
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

        public async Task<string> GetS3DownloadUrlAsync(string s3Key)
        {
            var req = NewRequest(HttpMethod.Get,
                "/api/v1/s3/download/presigned?key=" + Uri.EscapeDataString(s3Key));
            string body = await SendAsync(req, "S3 presign");
            return JsonConvert.DeserializeObject<ApiEnvelope<S3PresignedDownloadDto>>(body).data.url;
        }

        // ── Release Part Code: metadata + preview ──────────────────────────────
        // These mirror the endpoints atlas-ui's release page calls so the plugin
        // form offers the same cascading dropdowns. The metadata docs are dynamic
        // dicts (major→{part_number_identifier, minor_groups}, models, etc.) so
        // they're parsed with JToken rather than rigid DTOs.

        // Builds a {value,label} option the same way atlas-ui's mapToOptions does:
        // "<part_number_identifier> - <key>", falling back to the bare key.
        private static NamedOption ToOption(string key, JToken meta)
        {
            string ident = meta?["part_number_identifier"]?.ToString();
            string label = string.IsNullOrEmpty(ident) ? key : $"{ident} - {key}";
            return new NamedOption(key, label);
        }

        private static JToken FirstDocData(string body)
        {
            // Envelope: { data: [ { data: {...} }, ... ] } → return data[0].data
            var data = JObject.Parse(body)["data"] as JArray;
            if (data == null || data.Count == 0) return null;
            return data[0]?["data"];
        }

        /// <summary>Model options for a vehicle category
        /// (GET /part-master/part-number/model-code/filters).</summary>
        public async Task<List<NamedOption>> FetchModelOptionsAsync(string vehicleCategory)
        {
            var req = NewRequest(HttpMethod.Get,
                "/api/v1/part-master/part-number/model-code/filters?vehicle_category="
                + Uri.EscapeDataString(vehicleCategory ?? ""));
            string body = await SendAsync(req, "Fetch models");
            var models = FirstDocData(body)?["models"] as JObject;
            var options = new List<NamedOption>();
            if (models != null)
                foreach (var p in models.Properties())
                    options.Add(ToOption(p.Name, p.Value));
            return options;
        }

        /// <summary>Major groups + the minor groups nested under each, for a
        /// project + vehicle category (GET /part-master/part-number/group/filters).</summary>
        public async Task<GroupTreeDto> FetchGroupTreeAsync(string projectIdentifier, string vehicleCategory)
        {
            var qs = "project_identifier=" + Uri.EscapeDataString(projectIdentifier ?? "");
            if (!string.IsNullOrEmpty(vehicleCategory))
                qs += "&vehicle_category=" + Uri.EscapeDataString(vehicleCategory);
            var req = NewRequest(HttpMethod.Get,
                "/api/v1/part-master/part-number/group/filters?" + qs);
            string body = await SendAsync(req, "Fetch groups");

            var tree = new GroupTreeDto();
            var majors = FirstDocData(body) as JObject;
            if (majors != null)
            {
                foreach (var mp in majors.Properties())
                {
                    tree.Majors.Add(ToOption(mp.Name, mp.Value));
                    var minors = new List<NamedOption>();
                    if (mp.Value?["minor_groups"] is JObject minorObj)
                        foreach (var np in minorObj.Properties())
                            minors.Add(ToOption(np.Name, np.Value));
                    tree.MinorsByMajor[mp.Name] = minors;
                }
            }
            return tree;
        }

        /// <summary>All aggregate / sub-aggregate pairings
        /// (GET /part-master/aggregate-config).</summary>
        public async Task<List<AggregateConfigDto>> FetchAggregateConfigsAsync()
        {
            var req = NewRequest(HttpMethod.Get, "/api/v1/part-master/aggregate-config");
            string body = await SendAsync(req, "Fetch aggregate config");
            // This endpoint returns a RAW JSON array (it `return`s the list rather
            // than going through the RequestSuccess envelope), but tolerate an
            // enveloped {data:[...]} shape too in case that ever changes.
            var token = JToken.Parse(body);
            JArray arr = token as JArray ?? token["data"] as JArray;
            return arr?.ToObject<List<AggregateConfigDto>>() ?? new List<AggregateConfigDto>();
        }

        /// <summary>Live preview of the next part_number that would be minted
        /// (POST /part-master/part-number/generate-next-part-number).</summary>
        public async Task<string> GenerateNextPartNumberAsync(
            string projectIdentifier, string vehicleCategory, string model,
            string majorGroup, string minorGroup, string releaseType)
        {
            var req = NewRequest(HttpMethod.Post,
                "/api/v1/part-master/part-number/generate-next-part-number");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new
                {
                    project_identifier = projectIdentifier,
                    vehicle_category = vehicleCategory,
                    model,
                    major_group = majorGroup,
                    minor_group = minorGroup,
                    release_type = releaseType,
                }),
                System.Text.Encoding.UTF8, "application/json");
            string body = await SendAsync(req, "Preview part number");
            return JsonConvert.DeserializeObject<ApiEnvelope<string>>(body).data;
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

        /// <summary>
        /// P7.49: presigns the S3 key, GETs JSON bytes, deserialises into T.
        /// Returns null on any failure — callers treat null as "no manifest
        /// available, fall back to the Resolve-from-Atlas flow".
        /// </summary>
        public async Task<T> DownloadJsonByS3KeyAsync<T>(string s3Key) where T : class
        {
            if (string.IsNullOrEmpty(s3Key)) return null;
            try
            {
                string url = await GetS3DownloadUrlAsync(s3Key);
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _s3Http.SendAsync(req))
                {
                    resp.EnsureSuccessStatusCode();
                    string body = await resp.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<T>(body);
                }
            }
            catch { return null; }
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
