using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AtlasCadCore.ApiClient
{
    /// <summary>
    /// Per-revision references to S3-stored files, keyed by file kind.
    /// The backend writes this shape; legacy revisions may have a JSON list
    /// instead — the JsonConverter below handles both.
    /// </summary>
    [JsonConverter(typeof(ReferenceDocumentsConverter))]
    public class ReferenceDocumentsDto
    {
        // PDF drawing.
        public string Drawing2d;
        // STEP file (.stp / .step) — visual / interchange.
        public string Step3d;
        // Native CAD file (.sldprt, .CATPart, .prt, ...) — editable.
        public string Native3dRaw;
    }

    internal class ReferenceDocumentsConverter : JsonConverter<ReferenceDocumentsDto>
    {
        public override ReferenceDocumentsDto ReadJson(JsonReader reader, Type objectType,
            ReferenceDocumentsDto existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.ReadFrom(reader);
            if (token.Type == JTokenType.Null) return null;

            var dto = new ReferenceDocumentsDto();
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                dto.Drawing2d = obj["2d"]?.Type == JTokenType.String ? obj["2d"].Value<string>() : null;
                dto.Step3d = obj["3d"]?.Type == JTokenType.String ? obj["3d"].Value<string>() : null;
                dto.Native3dRaw = obj["3d_raw"]?.Type == JTokenType.String ? obj["3d_raw"].Value<string>() : null;
                return dto;
            }
            if (token.Type == JTokenType.Array)
            {
                // Legacy list-shaped reference_documents — infer the kind
                // from each entry's extension and slot it into the typed
                // dto. Last-writer-wins per kind.
                foreach (var item in (JArray)token)
                {
                    if (item.Type != JTokenType.String) continue;
                    string key = item.Value<string>();
                    if (string.IsNullOrEmpty(key)) continue;
                    string ext = System.IO.Path.GetExtension(key).ToLowerInvariant();
                    if (ext == ".pdf") dto.Drawing2d = key;
                    else if (ext == ".stp" || ext == ".step") dto.Step3d = key;
                    else if (ext == ".sldprt" || ext == ".sldasm"
                          || ext == ".catpart" || ext == ".catproduct"
                          || ext == ".prt") dto.Native3dRaw = key;
                }
                return dto;
            }
            return null;
        }

        public override void WriteJson(JsonWriter writer, ReferenceDocumentsDto value, JsonSerializer serializer)
        {
            var obj = new JObject();
            obj["2d"] = value?.Drawing2d;
            obj["3d"] = value?.Step3d;
            obj["3d_raw"] = value?.Native3dRaw;
            obj.WriteTo(writer);
        }
    }

    public class ApiEnvelope<T>
    {
        public bool success;
        public string message;
        public int resp_code;
        public T data;
    }

    public class PaginatedDto<T>
    {
        public List<T> items;
        public int total;
        public int page;
        public int limit;
        public int pages;
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

    public class PluginVersionDto
    {
        public string version;
        public string installer_s3_key;
        public string download_url;
    }

    // ---- Part master ----

    public class PartMasterRevisionDto
    {
        public string part_number;
        public string created_by;
        public string created_at;
        // Nullable — older revisions never had this flag and return null.
        public bool? active;
        // Canonical storage: dict { "2d", "3d", "3d_raw" } → S3 key strings.
        // The JsonConverter on the type also tolerates legacy list-shaped
        // values (extension-inferred) so very old docs still deserialize.
        public ReferenceDocumentsDto reference_documents;

        [JsonIgnore]
        public ReferenceDocumentsDto EffectiveRefs => reference_documents
            ?? new ReferenceDocumentsDto();
    }

    /// <summary>
    /// One row in the paginated /part-master/part-number response. `_id` is
    /// the Mongo ObjectId as a string; `releases` is a map of release_type
    /// (PROTO/PRODUCTION/ALTERNATE_PART) to the list of revisions in that bucket.
    /// </summary>
    public class PartMasterDocumentDto
    {
        public string _id;
        public string project_identifier;
        public string major_group;
        public string minor_group;
        public string model;
        public string description;
        public string created_at;
        public string created_by;
        public Dictionary<string, List<PartMasterRevisionDto>> releases;
    }

    public class CheckoutResultDto
    {
        public string part_number;
        public string locked_by;
        public string locked_at;
    }

    public class MyCheckoutsDto
    {
        public List<CheckoutResultDto> checkouts;
    }

    public class S3PresignedDownloadDto
    {
        public string url;
        public string key;
    }

    // ---- Presigned-upload flow ----

    public class PresignUploadResultDto
    {
        public string session_id;
        public List<PresignedUploadEntryDto> uploads;
    }

    public class PresignedUploadEntryDto
    {
        public string filename;
        public string presigned_url;
        public string s3_key;
    }

    // ---- Upload / create-batch ----

    public class UploadResultDto
    {
        public List<UploadAttachedDto> attached;
        public List<MissingPartDto> missing_parts;
    }

    public class UploadAttachedDto
    {
        public string part_number;
        public ReferenceDocumentsDto reference_documents;
    }

    public class MissingPartDto
    {
        public string part_number;
        public string filename;
        public string step_filename;
        public string detected_description;
    }

    public class CreateBatchEntryDto
    {
        public string detected_part_number;
        public string project_identifier;
        public string vehicle_category;
        public string model;
        public string major_group;
        public string minor_group;
        public string release_type;
        public string description;
    }

    public class CreateBatchResultDto
    {
        public List<CreatedEntryDto> created;
    }

    public class CreatedEntryDto
    {
        public string detected_part_number;
        public string new_part_number;
        public string release_type;
    }

    // ---- Checkin ----

    public class CheckinPreviewResultDto
    {
        public string root_part_number;
        public string release_type;
        public List<CheckinToBumpDto> to_bump;
    }

    public class CheckinToBumpDto
    {
        public string part_number;
        public string reason;   // "changed" | "ancestor_of_changed"
        public int depth;
    }

    public class CheckinResultDto
    {
        public string root_part_number;
        public string release_type;
        public List<BumpedDto> bumped;
        public string comment;
    }

    public class BumpedDto
    {
        public string old_part_number;
        public string new_part_number;
    }
}
