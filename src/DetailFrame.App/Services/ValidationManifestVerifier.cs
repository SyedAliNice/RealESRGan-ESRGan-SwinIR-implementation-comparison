using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DetailFrame.App.Models;

namespace DetailFrame.App.Services;

/// <summary>
/// Mirrors worker/src/detailframe_worker/data/validation-manifest.schema.json and the worker's
/// shared reviewed-manifest allowlist so the app never enables a model the worker would reject.
/// </summary>
public static class ValidationManifestVerifier
{
    private static readonly HashSet<string> ManifestKeys =
    [
        "manifest_version", "model_id", "checkpoint_sha256", "weight_url", "license", "provenance_url",
        "architecture", "scale", "architecture_config", "architecture_config_sha256",
        "expected_state_dict", "device_validation",
    ];

    private static readonly HashSet<string> RecordKeys =
        ["status", "tested_at", "torch_version", "checkpoint_sha256"];

    public static string DefaultTrustPath =>
        Path.Combine(AppContext.BaseDirectory, "shared", "reviewed-manifests.json");

    public static IReadOnlyDictionary<string, string> LoadReviewedManifests(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray() : [];
            if (rootProperties.Length != 2
                || rootProperties.Select(p => p.Name).Distinct().Count() != 2
                || !root.TryGetProperty("schema_version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("manifests", out var manifests)
                || manifests.ValueKind != JsonValueKind.Object)
                throw new CatalogException("The reviewed manifest trust data has an unsupported format.");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in manifests.EnumerateObject())
            {
                if (!Regex.IsMatch(entry.Name, @"\A[0-9a-f]{64}\z")
                    || entry.Value.ValueKind != JsonValueKind.String
                    || !Regex.IsMatch(entry.Value.GetString()!, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")
                    || !result.TryAdd(entry.Name, entry.Value.GetString()!))
                    throw new CatalogException("The reviewed manifest trust data contains an invalid digest or model ID.");
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CatalogException($"The reviewed manifest trust data could not be read: {path}", ex);
        }
    }

    public static bool Verify(
        string manifestPath,
        ModelVariant variant,
        IReadOnlyDictionary<string, string> reviewed)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (!reviewed.TryGetValue(digest, out var reviewedId) || reviewedId != variant.Id)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return Matches(document.RootElement, variant);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Matches(JsonElement m, ModelVariant v)
    {
        if (m.ValueKind != JsonValueKind.Object
            || !m.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(ManifestKeys)
            || !m.GetProperty("manifest_version").TryGetInt32OrNull(out var version) || version != 1
            || !Eq(m, "model_id", v.Id)
            || !Eq(m, "checkpoint_sha256", v.Sha256)
            || !Eq(m, "weight_url", v.WeightUrl)
            || !Eq(m, "license", v.License)
            || !Eq(m, "provenance_url", v.ProvenanceUrl)
            || !Eq(m, "architecture", v.Architecture)
            || !m.GetProperty("scale").TryGetInt32OrNull(out var scale) || scale < 2 || scale != v.Scale
            || !ModelCatalogService.IsHttpsUrl(v.WeightUrl)
            || !ModelCatalogService.IsHttpsUrl(v.ProvenanceUrl))
        {
            return false;
        }

        var config = m.GetProperty("architecture_config");
        if (config.ValueKind != JsonValueKind.Object || !config.EnumerateObject().Any()
            || !Eq(m, "architecture_config_sha256", ConfigDigest(v.Architecture, scale, config)))
        {
            return false;
        }

        return StateDictOk(m.GetProperty("expected_state_dict")) && DevicesOk(m.GetProperty("device_validation"), v);
    }

    private static bool Eq(JsonElement parent, string key, string expected) =>
        parent.GetProperty(key) is { ValueKind: JsonValueKind.String } p
        && p.GetString() is { Length: > 0 } s && s == expected;

    private static bool StateDictOk(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Any())
        {
            return false;
        }

        foreach (var entry in e.EnumerateObject())
        {
            if (entry.Name.Length == 0
                || entry.Value.ValueKind != JsonValueKind.Array
                || entry.Value.GetArrayLength() == 0
                || entry.Value.EnumerateArray().Any(d => !d.TryGetInt64OrNull(out var n) || n < 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DevicesOk(JsonElement e, ModelVariant v)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = e.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count == 0
            || names.Any(n => n is not ("cpu" or "cuda"))
            || !names.ToHashSet().SetEquals(v.Devices)
            || !names.Contains("cpu"))
        {
            return false;
        }

        foreach (var record in e.EnumerateObject().Select(p => p.Value))
        {
            if (record.ValueKind != JsonValueKind.Object
                || !record.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(RecordKeys)
                || !Eq(record, "status", "passed")
                || record.GetProperty("torch_version") is not { ValueKind: JsonValueKind.String } torch
                || torch.GetString()!.Length == 0
                || !Eq(record, "checkpoint_sha256", v.Sha256)
                || record.GetProperty("tested_at") is not { ValueKind: JsonValueKind.String } date
                || !Regex.IsMatch(date.GetString()!, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$")
                || !DateOnly.TryParseExact(
                    date.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return false;
            }
        }

        return true;
    }

    // Reproduces the worker's json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False) digest.
    private static string ConfigDigest(string architecture, int scale, JsonElement config)
    {
        var sb = new StringBuilder();
        sb.Append("{\"architecture\":");
        AppendString(sb, architecture);
        sb.Append(",\"config\":");
        if (!AppendCanonical(sb, config))
        {
            return "";
        }

        sb.Append(",\"scale\":").Append(scale.ToString(CultureInfo.InvariantCulture)).Append('}');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static bool AppendCanonical(StringBuilder sb, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    AppendString(sb, p.Name);
                    sb.Append(':');
                    if (!AppendCanonical(sb, p.Value))
                    {
                        return false;
                    }
                }

                sb.Append('}');
                return true;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    if (!AppendCanonical(sb, item))
                    {
                        return false;
                    }
                }

                sb.Append(']');
                return true;
            case JsonValueKind.String:
                AppendString(sb, e.GetString()!);
                return true;
            case JsonValueKind.Number:
                // Only plain integers are canonicalised; floats are rejected rather than risk a digest mismatch.
                if (!e.TryGetInt64(out var n))
                {
                    return false;
                }

                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                return true;
            case JsonValueKind.True:
                sb.Append("true");
                return true;
            case JsonValueKind.False:
                sb.Append("false");
                return true;
            case JsonValueKind.Null:
                sb.Append("null");
                return true;
            default:
                return false;
        }
    }

    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
    }

    private static bool TryGetInt32OrNull(this JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static bool TryGetInt64OrNull(this JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }
}
