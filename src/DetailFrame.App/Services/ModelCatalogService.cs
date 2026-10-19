using System.Text.Json;
using System.Text.RegularExpressions;
using DetailFrame.App.Models;

namespace DetailFrame.App.Services;

/// <summary>
/// Validates shared/models.json against the rules in shared/models.schema.json in code,
/// so no schema-validator dependency is needed. Tests assert the patterns stay in sync.
/// </summary>
public sealed class ModelCatalogService
{
    public const string IdPattern = "^[a-z0-9]+(?:-[a-z0-9]+)*$";
    public const string Sha256Pattern = "^[0-9a-f]{64}$";
    public const string WeightFilePattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";
    public const string ReservedNamePattern =
        "^(?:[cC][oO][nN]|[pP][rR][nN]|[aA][uU][xX]|[nN][uU][lL]|[cC][oO][mM][1-9]|[lL][pP][tT][1-9])(?:\\..*)?$";
    public const string HttpsUrlPattern = "^(?i:https)://[^/@?#\\\\\\s]+(?:[/?#]|$)";
    public const string ManifestPattern = "^[A-Za-z0-9][A-Za-z0-9._-]*\\.json$";

    private static readonly HashSet<string> RequiredKeys =
    [
        "id", "display_name", "architecture", "scale", "weight_file", "weight_url",
        "sha256", "license", "provenance_url", "devices", "enabled",
    ];

    private static readonly HashSet<string> AllowedKeys =
        [.. RequiredKeys, "size_bytes", "validation_manifest"];

    private readonly string _catalogPath;

    public ModelCatalogService(string catalogPath)
    {
        _catalogPath = catalogPath;
    }

    public static string DefaultCatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "shared", "models.json");

    public async Task<IReadOnlyList<ModelVariant>> LoadEnabledModelsAsync(CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CatalogException("The model catalog could not be read.", ex);
        }

        return Parse(text);
    }

    public static IReadOnlyList<ModelVariant> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(p => p.Name is not ("schema_version" or "models"))
                || !root.TryGetProperty("schema_version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogException("The model catalog has an unsupported format.");
            }

            var result = new List<ModelVariant>();
            var seen = new HashSet<(string, int)>();
            foreach (var entry in models.EnumerateArray())
            {
                var variant = TryReadEntry(entry);
                if (variant is not null && seen.Add((variant.Id, variant.Scale)))
                {
                    result.Add(variant);
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new CatalogException("The model catalog is not valid JSON.", ex);
        }
    }

    // Returns null for malformed, disabled, or evidence-incomplete entries.
    private static ModelVariant? TryReadEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var keys = e.EnumerateObject().Select(p => p.Name).ToHashSet();
        if (!RequiredKeys.IsSubsetOf(keys) || !keys.IsSubsetOf(AllowedKeys))
        {
            return null;
        }

        if (!Str(e, "id", out var id) || !Regex.IsMatch(id, IdPattern)
            || !Str(e, "display_name", out var name) || name.Length == 0
            || !Str(e, "architecture", out var arch) || arch.Length == 0
            || e.GetProperty("scale").ValueKind != JsonValueKind.Number
            || !e.GetProperty("scale").TryGetInt32(out var scale) || scale < 2
            || !Str(e, "weight_file", out var file) || !Regex.IsMatch(file, WeightFilePattern)
            || Regex.IsMatch(file, ReservedNamePattern)
            || !Str(e, "weight_url", out var url) || !IsHttpsUrl(url)
            || !Str(e, "sha256", out var sha) || !Regex.IsMatch(sha, Sha256Pattern)
            || !Str(e, "license", out var license) || license.Length == 0
            || !Str(e, "provenance_url", out var provenance) || !IsHttpsUrl(provenance)
            || e.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        long? size = null;
        if (e.TryGetProperty("size_bytes", out var sizeElement))
        {
            if (sizeElement.ValueKind != JsonValueKind.Number
                || !sizeElement.TryGetInt64(out var s) || s < 1)
            {
                return null;
            }

            size = s;
        }

        var devicesElement = e.GetProperty("devices");
        if (devicesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var devices = new List<string>();
        foreach (var d in devicesElement.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.String
                || d.GetString() is not ("cpu" or "cuda")
                || devices.Contains(d.GetString()!))
            {
                return null;
            }

            devices.Add(d.GetString()!);
        }

        string? manifest = null;
        if (e.TryGetProperty("validation_manifest", out var manifestElement))
        {
            if (manifestElement.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(manifestElement.GetString()!, ManifestPattern))
            {
                return null;
            }

            manifest = manifestElement.GetString();
        }

        if (e.GetProperty("enabled").ValueKind != JsonValueKind.True)
        {
            return null;
        }

        if (manifest is null || !devices.Contains("cpu"))
        {
            return null;
        }

        return new ModelVariant(id, name, arch, scale, file, url, sha, license, provenance, size, devices, manifest);
    }

    private static bool Str(JsonElement e, string key, out string value)
    {
        var p = e.GetProperty(key);
        if (p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString()!;
            return true;
        }

        value = "";
        return false;
    }

    public static bool IsHttpsUrl(string value)
    {
        if (!Regex.IsMatch(value, HttpsUrlPattern)
            || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrEmpty(uri.Host)
            && uri.AbsolutePath.Length > 1;
    }
}
