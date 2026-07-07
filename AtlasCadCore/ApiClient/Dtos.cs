using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AtlasCadCore.ApiClient
{
    [JsonConverter(typeof(ReferenceDocumentsConverter))]
    public class ReferenceDocumentsDto
    {
        public string Drawing2d;
        public string Step3d;
        public string Native3dRaw;
        // P7.48/49 (reinstated by P7.57). Set only for assemblies uploaded
        // by the plugin. Plugin downloads this on checkout to pre-fetch
        // every child file BEFORE OpenDocument fires, so CATIA/SW find
        // every reference on disk and skip the broken-links dialog. R2025's
        // Product COM API does not expose broken-ref filenames at all, so
        // this manifest is the only way to resolve children on R2025.
        public string TreeJson;
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
                dto.TreeJson = obj["tree"]?.Type == JTokenType.String ? obj["tree"].Value<string>() : null;
                return dto;
            }
            if (token.Type == JTokenType.Array)
            {
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
                    else if (ext == ".json") dto.TreeJson = key;
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
            obj["tree"] = value?.TreeJson;
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

    public class PartMasterRevisionDto
    {
        public string part_number;
        public string created_by;
        public string created_at;
        public bool? active;
        public string status;
        public ReferenceDocumentsDto reference_documents;

        [JsonIgnore]
        public ReferenceDocumentsDto EffectiveRefs => reference_documents
            ?? new ReferenceDocumentsDto();
    }

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

    public class UploadResultDto
    {
        public List<UploadAttachedDto> attached;
        public List<MissingPartDto> missing_parts;
        /// <summary>Parts skipped because they already have a native (3d_raw) in
        /// Atlas — Upload won't overwrite; use Check In to revise.</summary>
        public List<MissingPartDto> already_present;
        /// <summary>Parts whose exact revision wasn't in Atlas but whose 8-char
        /// base matched an existing part_master, so Upload minted the file's
        /// own revision as a new ACTIVE revision (e.g. file AH1202950F created
        /// under base AH120295, demoting AH1202950A). Also included in
        /// <see cref="attached"/>; this list just lets the summary call them out.</summary>
        public List<UploadAttachedDto> new_revisions;
        /// <summary>Result of the backend's attempt to build/update assembly
        /// documents from the uploaded tree.json(s). Null if the backend didn't
        /// report it.</summary>
        public AssemblyIngestDto assembly_ingest;
    }

    public class AssemblyIngestDto
    {
        public string source;
        public int created;
        public int updated;
        public List<string> skipped;
        public List<string> missing_parts;
        public string error;
        public List<AssemblyIngestTreeDto> trees;
    }

    public class AssemblyIngestTreeDto
    {
        public string s3_key;
        public string root_part_number;
        public string error;
    }

    public class UploadAttachedDto
    {
        public string part_number;
        /// <summary>For a minted new revision, the previously-active part_number
        /// that was demoted (null for a plain attach).</summary>
        public string previous_part_number;
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
        // Full atlas-ui release field set (all optional server-side).
        public string aggregate;
        public string sub_aggregate;
        public string source;
        public bool? available_for_service;
    }

    /// <summary>One aggregate / sub-aggregate pairing from
    /// GET /part-master/aggregate-config.</summary>
    public class AggregateConfigDto
    {
        public string aggregate;
        public string sub_aggregate;
    }

    /// <summary>A {value,label} option for a metadata-driven dropdown. ToString
    /// returns the label so it renders directly in a WinForms ComboBox while the
    /// caller reads <see cref="Value"/> for the payload.</summary>
    public class NamedOption
    {
        public string Value;
        public string Label;
        public NamedOption() { }
        public NamedOption(string value, string label) { Value = value; Label = label; }
        public override string ToString() => Label ?? Value ?? "";
    }

    /// <summary>Major groups plus the minor groups nested under each, parsed
    /// from GET /part-master/part-number/group/filters (cascading dropdowns).</summary>
    public class GroupTreeDto
    {
        public List<NamedOption> Majors = new List<NamedOption>();
        public Dictionary<string, List<NamedOption>> MinorsByMajor =
            new Dictionary<string, List<NamedOption>>(System.StringComparer.OrdinalIgnoreCase);
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

    public class CheckinPreviewResultDto
    {
        public string root_part_number;
        public string release_type;
        public List<CheckinToBumpDto> to_bump;
    }

    public class CheckinToBumpDto
    {
        public string part_number;
        public string reason; 
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
