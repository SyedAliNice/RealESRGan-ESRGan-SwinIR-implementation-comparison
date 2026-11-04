using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class ModelCatalogServiceTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Entry(
        string id = "test-model", int scale = 4, bool enabled = true, string devices = "\"cpu\"",
        string url = "https://example.test/w.bin", string extra = "\"validation_manifest\":\"m.json\",",
        string file = "w.bin", string sha = Sha) =>
        $$"""
        {"id":"{{id}}","display_name":"Test","architecture":"rrdb","scale":{{scale}},"weight_file":"{{file}}",
         "weight_url":"{{url}}","sha256":"{{sha}}","license":"MIT","provenance_url":"https://example.test/p",
         {{extra}}"devices":[{{devices}}],"enabled":{{enabled.ToString().ToLowerInvariant()}}}
        """;

    private static string Catalog(params string[] entries) =>
        $$"""{"schema_version":1,"models":[{{string.Join(",", entries)}}]}""";


    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dfcat-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Manifest(
        string id = "test-model", int scale = 4, string sha = Sha, string devices = "cpu",
        string url = "https://example.test/w.bin", string? configSha = null, string torch = "2.4.0")
    {
        configSha ??= Hex($$"""{"architecture":"rrdb","config":{"nf":64},"scale":{{scale}}}""");
        var records = string.Join(",", devices.Split(',').Select(d =>
            "\"" + d + "\":{\"status\":\"passed\",\"tested_at\":\"2026-01-02\",\"torch_version\":\""
            + torch + "\",\"checkpoint_sha256\":\"" + sha + "\"}"));
        var dv = "{" + records + "}";
        return $$$"""
            {"manifest_version":1,"model_id":"{{{id}}}","checkpoint_sha256":"{{{sha}}}","weight_url":"{{{url}}}",
             "license":"MIT","provenance_url":"https://example.test/p","architecture":"rrdb","scale":{{{scale}}},
             "architecture_config":{"nf":64},"architecture_config_sha256":"{{{configSha}}}",
             "expected_state_dict":{"conv.weight":[64,3,3,3]},"device_validation":{{{dv}}}}
            """;
    }

    // Writes m.json (when given) and reviews its digest for test-model unless reviewedId is null.
    private IReadOnlyList<ModelVariant> Parse(
        string catalog, string? manifest = null, string? reviewedId = "test-model", string file = "m.json")
    {
        var reviewed = new Dictionary<string, string>();
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(_dir, file), manifest);
            if (reviewedId is not null)
            {
                reviewed[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant()] =
                    reviewedId;
            }
        }

        return ModelCatalogService.Parse(catalog, _dir, reviewed);
    }

    private static string RepoFile(string relative)
    {
        return PythonTestTools.RepoFile(relative);
    }

        private sealed class FixtureHandler(byte[] payload) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }

        [Fact]
        public async Task AppDownloadAndWorkerConsumeSameReviewedFixture()
        {
            var python = PythonTestTools.Python();
            var checkpoint = Path.Combine(_dir, "source.bin");
            var create = await PythonTestTools.RunAsync("-c",
                "import sys,torch;torch.save({'conv.weight':torch.ones((64,3,3,3))},sys.argv[1])", checkpoint);
            Assert.True(create.ExitCode == 0, "Install the worker dev dependencies. " + create.Error);

            var payload = await File.ReadAllBytesAsync(checkpoint);
            var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var manifest = Manifest(sha: sha);
            var catalogText = Catalog(Entry(sha: sha));
            await File.WriteAllTextAsync(Path.Combine(_dir, "m.json"), manifest);
            var digest = Hex(manifest);
            var trustPath = Path.Combine(_dir, "reviewed-manifests.json");
            await File.WriteAllTextAsync(trustPath, JsonSerializer.Serialize(new
            {
                schema_version = 1, manifests = new Dictionary<string, string> { [digest] = "test-model" }
            }));
            var reviewed = ValidationManifestVerifier.LoadReviewedManifests(trustPath);
            var variant = Assert.Single(ModelCatalogService.Parse(catalogText, _dir, reviewed));
            var catalogPath = Path.Combine(_dir, "models.json");
            await File.WriteAllTextAsync(catalogPath, catalogText);
            File.Copy(RepoFile(@"shared\models.schema.json"), Path.Combine(_dir, "models.schema.json"));
            var cache = Path.Combine(_dir, "cache");
            var download = new ModelDownloadService(new HttpClient(new FixtureHandler(payload)), cache);
            var installed = await download.DownloadAndVerifyAsync(variant, new Progress<DownloadProgress>(), CancellationToken.None);
            Assert.Equal(Path.Combine(cache, sha, variant.WeightFile), installed.Path);
            Assert.True(File.Exists(installed.Path), installed.Path);

            var script = """
                import os,sys
                from pathlib import Path
                from detailframe_worker import catalog,engine
                from detailframe_worker.worker import run_worker
                os.environ['DETAILFRAME_CATALOG_PATH'] = sys.argv[1]
                catalog._trust_path = lambda: Path(sys.argv[2])
                engine.ADAPTERS['rrdb'] = engine.ModelAdapter(lambda *args: None)
                cache = Path('\\\\?\\' + sys.argv[3])
                variant, = catalog.load_catalog(Path(sys.argv[1]))
                assert tuple(engine._load_state_dict(variant, cache)['conv.weight'].shape) == (64,3,3,3)
                raise SystemExit(run_worker(sys.stdin,sys.stdout,sys.stderr,cache))
                """;
            var launcher = new ProcessWorkerLauncher(python, cache, ["-c", script, catalogPath, trustPath, cache],
                Path.GetDirectoryName(RepoFile(@"worker\pyproject.toml")));
            WorkerCapabilities caps;
            try
            {
                caps = await new WorkerProcessClient(launcher, new WorkerClientOptions
                {
                    CapabilitiesTimeout = TimeSpan.FromSeconds(60)
                }).GetCapabilitiesAsync(CancellationToken.None);
            }
            catch (WorkerException ex)
            {
                throw new InvalidOperationException("Fixture worker failed: " + ex.Diagnostics, ex);
            }
            var advertised = Assert.Single(caps.Models);
            Assert.Equal(variant.Id, advertised.Id);
            Assert.Equal([variant.Scale], advertised.Scales);
            Assert.Equal(["cpu"], advertised.Devices);
        }

    [Fact]
    public async Task CatalogAcceptanceAgreesWithSharedSchemaForRepresentativeCases()
    {
        var fixturePath = RepoFile(@"tests\fixtures\catalog-parity.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var cases = fixture.RootElement.GetProperty("documents").EnumerateArray().ToArray();
        var result = await PythonTestTools.RunAsync("-c", """
            import json,sys
            from jsonschema import Draft202012Validator
            from detailframe_worker.catalog import _format_checker
            schema = json.load(open(sys.argv[1], encoding='utf-8'))
            fixture = json.load(open(sys.argv[2], encoding='utf-8'))
            v = Draft202012Validator(schema, format_checker=_format_checker())
            print(json.dumps([v.is_valid(case['document']) for case in fixture['documents']]))
            """, RepoFile(@"shared\models.schema.json"), fixturePath);
        Assert.True(result.ExitCode == 0, "Install the worker dev dependencies. " + result.Error);
        var schemaAccepts = JsonSerializer.Deserialize<bool[]>(result.Output)!;
        Assert.Equal(cases.Length, schemaAccepts.Length);
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            var expected = item.GetProperty("valid").GetBoolean();
            var name = item.GetProperty("name").GetString();
            Assert.True(expected == schemaAccepts[i], $"Schema case: {name}");
            bool accepted;
            try { accepted = Parse(item.GetProperty("document").GetRawText(), Manifest()).Count == 1; }
            catch (CatalogException) { accepted = false; }
            Assert.True(expected == accepted, $"C# case: {name}");
        }
    }

    [Fact]
    public void UrlValidationMatchesSharedRegressionCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(RepoFile(@"tests\fixtures\catalog-parity.json")));
        foreach (var item in fixture.RootElement.GetProperty("urls").EnumerateArray())
        {
            var value = item.GetProperty("value").GetString()!;
            Assert.True(item.GetProperty("valid").GetBoolean() == ModelCatalogService.IsHttpsUrl(value), value);
        }
    }

    [Fact]
    public void Valid_enabled_entry_is_returned()
    {
        var models = Parse(Catalog(Entry()), Manifest());
        var m = Assert.Single(models);
        Assert.Equal("test-model", m.Id);
        Assert.Equal(4, m.Scale);
    }

    [Fact]
    public void Missing_manifest_file_drops_entry()
    {
        Assert.Empty(Parse(Catalog(Entry())));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Malformed_manifest_drops_entry(string manifest)
    {
        Assert.Empty(Parse(Catalog(Entry()), manifest));
    }

    [Fact]
    public void Manifest_not_on_reviewed_allowlist_drops_entry()
    {
        Assert.Empty(Parse(Catalog(Entry()), Manifest(), reviewedId: null));
        Assert.Empty(Parse(Catalog(Entry()), Manifest(), reviewedId: "other-model"));
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry()), _dir,
            ValidationManifestVerifier.LoadReviewedManifests(RepoFile(@"shared\reviewed-manifests.json"))));
    }

    [Fact]
    public void Shipped_allowlist_is_empty()
    {
        Assert.Empty(ValidationManifestVerifier.LoadReviewedManifests(RepoFile(@"shared\reviewed-manifests.json")));
        Assert.Empty(ValidationManifestVerifier.LoadReviewedManifests(ValidationManifestVerifier.DefaultTrustPath));
    }

    [Fact]
    public void CatalogDirectoryCannotSupplyItsOwnTrust()
    {
        var manifest = Manifest();
        File.WriteAllText(Path.Combine(_dir, "m.json"), manifest);
        File.WriteAllText(Path.Combine(_dir, "reviewed-manifests.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, manifests = new Dictionary<string, string> { [Hex(manifest)] = "test-model" }
        }));
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry()), _dir));
    }

    [Fact]
    public void TrustDataUsesStrictSharedContract()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(RepoFile(@"tests\fixtures\trust-data-cases.json")));
        var path = Path.Combine(_dir, "trust.json");
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            File.WriteAllText(path, item.GetProperty("raw").GetString()!);
            var name = item.GetProperty("name").GetString();
            if (item.GetProperty("valid").GetBoolean())
            {
                var data = ValidationManifestVerifier.LoadReviewedManifests(path);
                if (name == "synthetic") Assert.Equal("test-model", data[new string('a', 64)]);
            }
            else
                Assert.Throws<CatalogException>(() => ValidationManifestVerifier.LoadReviewedManifests(path));
        }
        File.Delete(path);
        Assert.Contains("trust data", Assert.Throws<CatalogException>(() =>
            ValidationManifestVerifier.LoadReviewedManifests(path)).Message);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("url")]
    [InlineData("config")]
    [InlineData("device")]
    [InlineData("id")]
    [InlineData("torch")]
    public void Manifest_evidence_mismatch_drops_entry(string kind)
    {
        var other = new string('a', 64);
        var manifest = kind switch
        {
            "sha" => Manifest(sha: other),
            "url" => Manifest(url: "https://example.test/other.bin"),
            "config" => Manifest(configSha: other),
            "device" => Manifest(devices: "cuda"),
            "id" => Manifest(id: "other-model"),
            _ => Manifest(torch: ""),
        };
        Assert.Empty(Parse(Catalog(Entry()), manifest));
    }

    [Fact]
    public void Manifest_missing_a_required_field_or_cpu_validation_drops_entry()
    {
        Assert.Empty(Parse(Catalog(Entry()), Manifest().Replace("\"license\":\"MIT\",", "")));
        Assert.Empty(Parse(Catalog(Entry(devices: "\"cpu\",\"cuda\"")), Manifest()));
    }

    [Fact]
    public void Valid_manifest_with_both_devices_enables_entry()
    {
        var m = Assert.Single(Parse(Catalog(Entry(devices: "\"cpu\",\"cuda\"")), Manifest(devices: "cpu,cuda")));
        Assert.Equal(new[] { "cpu", "cuda" }, m.Devices);
    }

    [Theory]
    [InlineData("https://example.test/a?x=1")]
    public void Valid_schema_urls_are_accepted(string url)
    {
        Assert.True(ModelCatalogService.IsHttpsUrl(url));
        Assert.Single(Parse(Catalog(Entry(url: url)), Manifest(url: url)));
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("https://example.test")]
    public void Bare_urls_match_worker_rejection(string url)
    {
        Assert.False(ModelCatalogService.IsHttpsUrl(url));
    }

    [Fact]
    public void DuplicateIdsAcrossScalesRejectWholeCatalog()
    {
        Assert.Throws<CatalogException>(() => Parse(
            Catalog(Entry(scale: 4), Entry(scale: 2)), Manifest()));
    }

    [Fact]
    public void Disabled_entries_are_excluded()
    {
        Assert.Empty(Parse(Catalog(Entry(enabled: false)), Manifest()));
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
        Assert.Empty(Parse(Catalog(Entry(id: id, scale: scale, url: url, file: file, sha: sha)), Manifest()));
    }

    [Fact]
    public void Enabled_entry_without_manifest_or_cpu_is_rejected()
    {
        Assert.Empty(Parse(Catalog(Entry(extra: "")), Manifest()));
        Assert.Empty(Parse(Catalog(Entry(devices: "\"cuda\"")), Manifest(devices: "cuda")));
    }

    [Fact]
    public void Unknown_properties_and_bad_types_are_rejected_without_throwing()
    {
        Assert.Empty(Parse(Catalog(Entry(extra: "\"validation_manifest\":\"m.json\",\"x\":1,")), Manifest()));
        Assert.Empty(Parse(Catalog(Entry().Replace("\"scale\":4", "\"scale\":\"4\"")), Manifest()));
        Assert.Empty(Parse(Catalog("42", "null")));
    }

    [Fact]
    public void Malformed_entry_invalidates_the_entire_catalog()
    {
        var models = Parse(Catalog(Entry(id: "Bad"), Entry(id: "good", scale: 2)), Manifest(id: "good", scale: 2), "good");
        Assert.Empty(models);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema_version\":2,\"models\":[]}")]
    [InlineData("{\"models\":[]}")]
    [InlineData("[]")]
    public void Malformed_catalog_throws(string text)
    {
        Assert.Throws<CatalogException>(() => ModelCatalogService.Parse(text, _dir, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Shipped_catalog_is_empty_and_loads()
    {
        var service = new ModelCatalogService(RepoFile(@"shared\models.json"));
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
        using var schema = JsonDocument.Parse(File.ReadAllText(RepoFile(@"shared\models.schema.json")));
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
