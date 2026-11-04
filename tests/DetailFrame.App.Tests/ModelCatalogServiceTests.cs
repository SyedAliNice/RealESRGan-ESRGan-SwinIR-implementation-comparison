using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DetailFrame.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }

        private sealed class FixtureHandler(byte[] payload) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }

        [Fact]
        public async Task AppDownloadAndWorkerConsumeSameReviewedFixture()
        {
            var python = RepoFile(@"worker\.venv\Scripts\python.exe");
            if (!File.Exists(python)) return;
            var checkpoint = Path.Combine(_dir, "source.bin");
            var create = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
            create.ArgumentList.Add("-c");
            create.ArgumentList.Add("import sys,torch;torch.save({'conv.weight':torch.ones((64,3,3,3))},sys.argv[1])");
            create.ArgumentList.Add(checkpoint);
            using (var process = Process.Start(create)!)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(0, process.ExitCode);
            }

            var payload = await File.ReadAllBytesAsync(checkpoint);
            var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var manifest = Manifest(sha: sha);
            var catalogText = Catalog(Entry(sha: sha));
            var variant = Assert.Single(Parse(catalogText, manifest));
            var catalogPath = Path.Combine(_dir, "models.json");
            await File.WriteAllTextAsync(catalogPath, catalogText);
            File.Copy(RepoFile(@"shared\models.schema.json"), Path.Combine(_dir, "models.schema.json"));
            var cache = Path.Combine(_dir, "cache");
            var download = new ModelDownloadService(new HttpClient(new FixtureHandler(payload)), cache);
            var installed = await download.DownloadAndVerifyAsync(variant, new Progress<DownloadProgress>(), CancellationToken.None);
            Assert.Equal(Path.Combine(cache, sha, variant.WeightFile), installed.Path);
            Assert.True(File.Exists(installed.Path), installed.Path);

            var digest = Hex(manifest);
            var script = """
                import os,sys
                from pathlib import Path
                from detailframe_worker import catalog,engine
                from detailframe_worker.worker import run_worker
                os.environ['DETAILFRAME_CATALOG_PATH'] = sys.argv[1]
                catalog.REVIEWED_VALIDATION_MANIFESTS[sys.argv[2]] = 'test-model'
                engine.ADAPTERS['rrdb'] = engine.ModelAdapter(lambda *args: None)
                cache = Path('\\\\?\\' + sys.argv[3])
                variant, = catalog.load_catalog(Path(sys.argv[1]))
                assert tuple(engine._load_state_dict(variant, cache)['conv.weight'].shape) == (64,3,3,3)
                raise SystemExit(run_worker(sys.stdin,sys.stdout,sys.stderr,cache))
                """;
            var launcher = new ProcessWorkerLauncher(python, cache, ["-c", script, catalogPath, digest, cache],
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
        var python = RepoFile(@"worker\.venv\Scripts\python.exe");
        if (!File.Exists(python)) return;
        var entry = Entry();
        var documents = new[]
        {
            Catalog(entry),
            Catalog(entry.Replace("\"id\":\"test-model\"", "\"id\":\"Bad_Id\"")),
            Catalog(entry.Replace("\"scale\":4", "\"scale\":1")),
            Catalog(entry.Replace("\"weight_file\":\"w.bin\"", "\"weight_file\":\"CON.pth\"")),
            Catalog(entry.Replace("\"weight_url\":\"https://example.test/w.bin\"", "\"weight_url\":\"https://example.test/\"")),
            Catalog(entry.Replace("\"devices\":[\"cpu\"]", "\"devices\":[\"cuda\"]")),
            Catalog(entry.Replace("\"sha256\":\"" + Sha + "\"", "\"sha256\":\"bad\"")),
            Catalog(entry.Replace("\"enabled\":true", "\"enabled\":true,\"unknown\":1")),
            Catalog(entry.Replace("\"validation_manifest\":\"m.json\",", "")),
        };
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(RepoFile(@"worker\pyproject.toml"))!
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import json,sys
            from jsonschema import Draft202012Validator
            from detailframe_worker.catalog import _format_checker
            schema = json.load(open(sys.argv[1], encoding='utf-8'))
            v = Draft202012Validator(schema, format_checker=_format_checker())
            print(json.dumps([v.is_valid(json.loads(d)) for d in json.load(sys.stdin)]))
            """);
        start.ArgumentList.Add(RepoFile(@"shared\models.schema.json"));
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(documents));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, error);
        var schemaAccepts = JsonSerializer.Deserialize<bool[]>(output)!;
        Assert.Equal(documents.Select(document => Parse(document, Manifest()).Count == 1), schemaAccepts);
        Assert.True(schemaAccepts[0]);
        Assert.All(schemaAccepts.Skip(1), accepted => Assert.False(accepted));
    }

    [Fact]
    public async Task UrlValidationMatchesWorkerAcrossHostAndPathCases()
    {
        var python = RepoFile(@"worker\.venv\Scripts\python.exe");
        if (!File.Exists(python)) return;
        var urls = new[]
        {
            "https://", "https:///missing-host", "https://user@models.example.com/file",
            "https://models.example.com:bad/file", "https://models.example.com:99999/file",
            "https://models.example.com", "https://models.example.com/",
            "https://models.example.com\\evil/file", "https://models example.com/file",
            "https://-models.example.com/file", "https://models-.example.com/file",
            "https://models..example.com/file", "https://models_example.com/file",
            "https://[::1/file", "https://[not-an-ip]/file", "https://::1/file",
            "https://999.999.1.1/file", "https://models.example.com:/file",
            "https://models.example.com:0/file", "https://models.example.com:65536/file",
            "HTTPS://models.example.com:8443/file?token=example#download",
            "https://xn--bcher-kva.example/file", "https://bücher.example/file",
            "https://192.0.2.1/file", "https://[2001:db8::1]:443/file",
            "https://example.com/a\\b", "https://example.com/.", "https://example.com/a/../",
        };
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(RepoFile(@"worker\pyproject.toml"))!
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import json,sys;from detailframe_worker.catalog import _is_https_url;print(json.dumps([_is_https_url(u) for u in json.load(sys.stdin)]))");
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(urls));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, error);
        var worker = JsonSerializer.Deserialize<bool[]>(output)!;
        for (var i = 0; i < urls.Length; i++)
            Assert.True(worker[i] == ModelCatalogService.IsHttpsUrl(urls[i]), urls[i]);
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
        Assert.Empty(ModelCatalogService.Parse(Catalog(Entry()), _dir));
    }

    [Fact]
    public void Shipped_allowlist_is_empty()
    {
        Assert.Empty(ValidationManifestVerifier.ReviewedManifests);
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
        Assert.Throws<CatalogException>(() => ModelCatalogService.Parse(text, _dir));
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
