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

    public class VersionSummaryDto
    {
        public int version_number;
        public string uploaded_at;
        public string uploaded_by;
        public string comment;
        public int file_count;
        public List<string> revision_bumps;
        public bool is_current;
    }

    public class VersionDetailDto
    {
        public string assembly_id;
        public string name;
        public int version_number;
        public string uploaded_at;
        public string uploaded_by;
        public string comment;
        public List<string> revision_bumps;
        public bool is_current;
        public List<CheckoutFileDto> files;
    }

    public class ApiEnvelope<T>
    {
        public bool success;
        public string message;
        public int resp_code;
        public T data;
    }

    public class PartLookupResult
    {
        public List<string> found;
        public List<string> missing;
        public List<PartLookupDetail> details;
    }

    public class PartLookupDetail
    {
        public string part_master_id;
        public string part_number;
        public bool active;
    }

    public class CheckinPreviewRow
    {
        public string part_number;
        public string filename;
        public string incoming_sha256;
        public string previous_sha256;
    }

    public class CheckinPreviewResult
    {
        public string assembly_id;
        public int based_on_version;
        public List<CheckinPreviewRow> changed;
        public List<CheckinPreviewRow> unchanged;
        public List<CheckinPreviewRow> added;
    }

    public class RevisionBump
    {
        public string part_number;
        public string release_type = "PRODUCTION";
    }

    public class InsertablePartDto
    {
        public string part_master_id;
        public string part_number;
        public string release_type;
        public string description;
        public string filename;
        public long size_bytes;
    }

    public class InsertUrlDto
    {
        public string part_number;
        public string filename;
        public string download_url;
        public long size_bytes;
    }

    public class PluginVersionDto
    {
        public string version;
        public string installer_s3_key;
        public string download_url;
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

        /// <summary>
        /// Retry transient network errors with exponential back-off. 401 and
        /// non-2xx responses are NOT retried — only true transport failures
        /// (DNS, TCP reset, TLS handshake glitch).
        /// </summary>
        private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, string operation, int attempts = 3)
        {
            Exception last = null;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    var req = requestFactory();
                    return await SendAsync(req, operation);
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                    if (i < attempts - 1)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
                catch (TaskCanceledException ex) when (!(ex is OperationCanceledException))
                {
                    // HttpClient timeout (not user cancellation).
                    last = ex;
                    if (i < attempts - 1)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            throw last ?? new Exception($"{operation} failed after {attempts} attempts");
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

        public async Task<PartLookupResult> LookupPartNumbersAsync(List<string> partNumbers)
        {
            var req = NewRequest(HttpMethod.Post, "/api/v1/cad/part-master/lookup");
            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { part_numbers = partNumbers }),
                System.Text.Encoding.UTF8,
                "application/json");
            string body = await SendAsync(req, "Part lookup");
            return JsonConvert.DeserializeObject<ApiEnvelope<PartLookupResult>>(body).data;
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

        public async Task<List<InsertablePartDto>> SearchInsertablePartsAsync(string query, int limit = 50)
        {
            string q = Uri.EscapeDataString(query ?? "");
            var req = NewRequest(HttpMethod.Get, $"/api/v1/cad/parts/search?q={q}&limit={limit}");
            string body = await SendAsync(req, "Search parts");
            return JsonConvert.DeserializeObject<ApiEnvelope<List<InsertablePartDto>>>(body).data
                ?? new List<InsertablePartDto>();
        }

        public async Task<InsertUrlDto> GetInsertUrlAsync(string partNumber)
        {
            var req = NewRequest(HttpMethod.Get, $"/api/v1/cad/parts/{partNumber}/insert-url");
            string body = await SendAsync(req, "Get insert URL");
            return JsonConvert.DeserializeObject<ApiEnvelope<InsertUrlDto>>(body).data;
        }

        public async Task<UploadResultDto> SetActiveVersionAsync(string assemblyId, int versionNumber)
        {
            var req = NewRequest(HttpMethod.Post,
                $"/api/v1/cad/assembly/{assemblyId}/set-active-version/{versionNumber}");
            string body = await SendAsync(req, "Set active version");
            return JsonConvert.DeserializeObject<ApiEnvelope<UploadResultDto>>(body).data;
        }

        public async Task<List<VersionSummaryDto>> ListVersionsAsync(string assemblyId)
        {
            var req = NewRequest(HttpMethod.Get, $"/api/v1/cad/assembly/{assemblyId}/versions");
            string body = await SendAsync(req, "List versions");
            return JsonConvert.DeserializeObject<ApiEnvelope<List<VersionSummaryDto>>>(body).data
                ?? new List<VersionSummaryDto>();
        }

        public async Task<VersionDetailDto> GetVersionAsync(string assemblyId, int versionNumber)
        {
            var req = NewRequest(HttpMethod.Get, $"/api/v1/cad/assembly/{assemblyId}/versions/{versionNumber}");
            string body = await SendAsync(req, "Get version");
            return JsonConvert.DeserializeObject<ApiEnvelope<VersionDetailDto>>(body).data;
        }

        public async Task<CheckoutResultDto> CheckoutAsync(string assemblyId)
        {
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/assembly/{assemblyId}/checkout");
            string body = await SendAsync(req, "Checkout");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckoutResultDto>>(body).data;
        }

        public async Task<CheckinPreviewResult> CheckinPreviewAsync(string assemblyId, List<AssemblyFileRef> tree)
        {
            var treePayload = new
            {
                files = tree.ConvertAll(t => new
                {
                    relative_path = t.RelativePath.Replace('\\', '/'),
                    filename = t.Filename,
                    is_root = t.IsRoot,
                    part_number = t.PartNumber,
                    sha256 = t.Sha256,
                })
            };
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonConvert.SerializeObject(treePayload)), "tree_json");
            var req = NewRequest(HttpMethod.Post, $"/api/v1/cad/assembly/{assemblyId}/checkin/preview");
            req.Content = form;
            string body = await SendAsync(req, "Checkin preview");
            return JsonConvert.DeserializeObject<ApiEnvelope<CheckinPreviewResult>>(body).data;
        }

        public async Task<UploadResultDto> CheckinAsync(
            string assemblyId,
            List<AssemblyFileRef> tree,
            List<RevisionBump> revisionBumps,
            string comment)
        {
            using (var content = BuildMultipart(name: null, tree: tree, comment: null))
            {
                var checkinPayload = new
                {
                    revision_bumps = revisionBumps ?? new List<RevisionBump>(),
                    comment = comment ?? "",
                };
                content.Add(new StringContent(JsonConvert.SerializeObject(checkinPayload)), "checkin_json");

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
            //
            // Three attempts with backoff so a single dropped connection (common
            // in flaky office Wi-Fi) doesn't fail the whole checkout.
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

        private MultipartFormDataContent BuildMultipart(string name, List<AssemblyFileRef> tree, string comment)
        {
            var content = new MultipartFormDataContent();

            if (name != null)
                content.Add(new StringContent(name), "name");

            // tree_json carries per-file metadata so the backend can rebuild
            // the folder structure, identify the root, and link each file to
            // its part_master_library entry (via part_number).
            var treePayload = new
            {
                files = tree.ConvertAll(t => new {
                    relative_path = t.RelativePath.Replace('\\', '/'),
                    filename = t.Filename,
                    is_root = t.IsRoot,
                    part_number = t.PartNumber,
                    sha256 = t.Sha256,
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
