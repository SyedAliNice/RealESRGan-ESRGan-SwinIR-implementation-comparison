using System.Text.Json;
using System.Text.RegularExpressions;
using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public class ModelCatalogServiceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Entry(
        string id = "test-model", int scale = 4, bool enabled = true, string devices = "\"cpu\",\"cuda\"",
        string url = "https://example.test/w.bin", string extra = "\"validation_manifest\":\"m.json\",",
        string file = "w.bin", string sha = Sha) =>
        $$"""
        {"id":"{{id}}","display_name":"Test","architecture":"rrdb","scale":{{scale}},"weight_file":"{{file}}",
         "weight_url":"{{url}}","sha256":"{{sha}}","license":"MIT","provenance_url":"https://example.test/p",
         {{extra}}"devices":[{{devices}}],"enabled":{{enabled.ToString().ToLowerInvariant()}}}
        """;

    private static string Catalog(params string[] entries) =>
        $$"""{"schema_version":1,"models":[{{string.Join(",", entries)}}]}""";

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DetailFrame.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void Valid_enabled_entry_is_returned()
    {
        var models = ModelCatalogService.Parse(Catalog(Entry()));
        var m = Assert.Single(models);
        Assert.Equal("test-model", m.Id);
        Assert.Equal(4, m.Scale);
    }

    [Fact]
    public void Disabled_entries_are_excluded()
    {
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry(enabled: false))));
    }

    [Theory]
    [InlineData("Bad_Id", 4, "https://example.test/w.bin", "w.bin", Sha)]
    [InlineData("ok", 1, "https://example.test/w.bin", "w.bin", Sha)]
    [InlineData("ok", 4, "http://example.test/w.bin", "w.bin", Sha)]
    [InlineData("ok", 4, "https://user@example.test/w.bin", "w.bin", Sha)]
    [InlineData("ok", 4, "https://example.test/w.bin", "con.bin", Sha)]
    [InlineData("ok", 4, "https://example.test/w.bin", "../w.bin", Sha)]
    [InlineData("ok", 4, "https://example.test/w.bin", "w.bin", "ABC")]
    public void Malformed_entries_are_rejected(string id, int scale, string url, string file, string sha)
    {
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry(id: id, scale: scale, url: url, file: file, sha: sha))));
    }

    [Fact]
    public void Enabled_entry_without_manifest_or_cpu_is_rejected()
    {
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry(extra: ""))));
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry(devices: "\"cuda\""))));
    }

    [Fact]
    public void Unknown_properties_and_bad_types_are_rejected_without_throwing()
    {
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry(extra: "\"validation_manifest\":\"m.json\",\"x\":1,"))));
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry().Replace("\"scale\":4", "\"scale\":\"4\""))));
        Assert.Empty(ModelCatalogService.Parse(Catalog("42", "null")));
    }

    [Fact]
    public void Valid_entries_survive_next_to_malformed_ones()
    {
        var models = ModelCatalogService.Parse(Catalog(Entry(id: "Bad"), Entry(id: "good", scale: 2)));
        Assert.Equal("good", Assert.Single(models).Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema_version\":2,\"models\":[]}")]
    [InlineData("{\"models\":[]}")]
    [InlineData("[]")]
    public void Malformed_catalog_throws(string text)
    {
        Assert.Throws<CatalogException>(() => ModelCatalogService.Parse(text));
    }

    [Fact]
    public async Task Shipped_catalog_is_empty_and_loads()
    {
        var service = new ModelCatalogService(RepoFile("shared/models.json"));
        Assert.Empty(await service.LoadEnabledModelsAsync());
    }

    [Fact]
    public async Task Missing_file_throws_catalog_exception()
    {
        var service = new ModelCatalogService(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"));
        await Assert.ThrowsAsync<CatalogException>(() => service.LoadEnabledModelsAsync());
    }

    [Fact]
    public void Code_patterns_match_schema_and_dotnet_handles_inline_case_groups()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(RepoFile("shared/models.schema.json")));
        var defs = schema.RootElement.GetProperty("$defs");
        var model = defs.GetProperty("model").GetProperty("properties");
        string P(JsonElement e) => e.GetProperty("pattern").GetString()!;

        Assert.Equal(ModelCatalogService.IdPattern, P(model.GetProperty("id")));
        Assert.Equal(ModelCatalogService.Sha256Pattern, P(defs.GetProperty("sha256")));
        Assert.Equal(ModelCatalogService.WeightFilePattern, P(model.GetProperty("weight_file")));
        Assert.Equal(ModelCatalogService.ReservedNamePattern, P(model.GetProperty("weight_file").GetProperty("not")));
        Assert.Equal(ModelCatalogService.HttpsUrlPattern, P(model.GetProperty("weight_url")));
        Assert.Equal(ModelCatalogService.HttpsUrlPattern, P(model.GetProperty("provenance_url")));
        Assert.Equal(ModelCatalogService.ManifestPattern, P(defs.GetProperty("validation_manifest_file")));

        var https = new Regex(P(model.GetProperty("weight_url")));
        Assert.Matches(https, "https://example.test/x");
        Assert.Matches(https, "HTTPS://example.test");
        Assert.DoesNotMatch(https, "http://example.test/x");
        Assert.DoesNotMatch(https, "https://a@example.test/x");
        Assert.DoesNotMatch(https, "ftp://example.test/x");
    }

    private static readonly ModelVariant Cpu4 = V("a", 4, "cpu");
    private static readonly ModelVariant Both2 = V("a", 2, "cpu", "cuda");
    private static readonly ModelVariant Other = V("b", 4, "cpu");

    private static ModelVariant V(string id, int scale, params string[] devices) =>
        new(id, id, "rrdb", scale, "w.bin", "https://e.test/w", Sha, "MIT", "https://e.test/p", null, devices, "m.json");

    [Fact]
    public void Empty_catalog_selector_has_no_selection()
    {
        var s = new ModelSelector([]);
        Assert.False(s.HasModels);
        Assert.Null(s.SelectedVariant);
        Assert.False(s.Resolve().IsUsable);
        Assert.Empty(s.AvailableDevices);
    }

    [Fact]
    public void Defaults_to_first_model_scale_and_auto()
    {
        var s = new ModelSelector([Cpu4, Both2, Other]);
        Assert.Equal("a", s.SelectedModelId);
        Assert.Equal(4, s.SelectedScale);
        Assert.Equal(DeviceChoice.Auto, s.SelectedDevice);
        Assert.Equal(new[] { 2, 4 }, s.ScalesFor("a"));
    }

    [Fact]
    public void Scale_limits_device_choices_and_normalizes_selection()
    {
        var s = new ModelSelector([Cpu4, Both2]);
        Assert.Equal(new[] { "auto", "cpu" }, s.AvailableDevices);
        Assert.False(s.SelectDevice("cuda"));
        Assert.True(s.SelectScale(2));
        Assert.True(s.SelectDevice("cuda"));
        Assert.True(s.SelectScale(4));
        Assert.Equal(DeviceChoice.Auto, s.SelectedDevice);
        Assert.False(s.SelectScale(8));
        Assert.False(s.SelectModel("zzz"));
    }

    [Fact]
    public void Auto_uses_cuda_only_when_worker_advertises_exact_combination()
    {
        var s = new ModelSelector([Both2], (id, scale, dev) => id == "a" && scale == 2 && dev == "cuda");
        Assert.Equal("cuda", s.Resolve().EffectiveDevice);
        var none = new ModelSelector([Both2], (_, _, _) => false);
        Assert.Equal("cpu", none.Resolve().EffectiveDevice);
        var wrongScale = new ModelSelector([Both2], (_, scale, _) => scale == 4);
        Assert.Equal("cpu", wrongScale.Resolve().EffectiveDevice);
    }

    [Fact]
    public void Explicit_cuda_unavailable_blocks_instead_of_falling_back()
    {
        var s = new ModelSelector([Both2], (_, _, _) => false);
        Assert.True(s.SelectDevice("cuda"));
        var r = s.Resolve();
        Assert.False(r.IsUsable);
        Assert.Contains("CUDA", r.BlockingMessage);
    }
}
