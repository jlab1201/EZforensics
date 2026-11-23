using System.Text.Json;
using EzForensics.Core.Assistant;
using EzForensics.Core.Case;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class AssistantConsentTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Store => Path.Combine(_t.Path, "store");
    private string Case(string name = "case1") => Path.Combine(_t.Path, name);

    [Fact]
    public void Consent_round_trip_per_provider()
    {
        AssistantConsent.Has(Case(), "anthropic", null, Store).Should().BeFalse();
        AssistantConsent.Grant(Case(), "anthropic", "some-model", null, Store);
        AssistantConsent.Has(Case(), "anthropic", null, Store).Should().BeTrue();
        AssistantConsent.Has(Case(), "openai", null, Store).Should().BeFalse();
        AssistantConsent.Has(Case("case2"), "anthropic", null, Store).Should().BeFalse();
    }

    [Fact]
    public void Consent_path_is_normalized()
    {
        AssistantConsent.Grant(Case() + Path.DirectorySeparatorChar, "anthropic", "m", null, Store);
        AssistantConsent.Has(Case().ToUpperInvariant(), "anthropic", null, Store).Should().BeTrue();
    }

    [Fact]
    public void Custom_base_url_change_asks_again()
    {
        AssistantConsent.Grant(Case(), "custom", "m", "https://a.example/v1", Store);
        AssistantConsent.Has(Case(), "custom", "https://a.example/v1/", Store).Should().BeTrue();
        AssistantConsent.Has(Case(), "custom", "https://b.example/v1", Store).Should().BeFalse();
    }

    [Fact]
    public void No_case_is_its_own_scope()
    {
        AssistantConsent.Has(null, "anthropic", null, Store).Should().BeFalse();
        AssistantConsent.Grant(null, "anthropic", "m", null, Store);
        AssistantConsent.Has("", "anthropic", null, Store).Should().BeTrue();
        AssistantConsent.Has(Case(), "anthropic", null, Store).Should().BeFalse();
    }

    [Fact]
    public void Store_records_who_when_provider_and_model()
    {
        AssistantConsent.Grant(Case(), "anthropic", "some-model", null, Store);
        var file = Path.Combine(Store, AssistantConsent.StoreFileName);
        File.Exists(file).Should().BeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement[0];
        r.GetProperty("provider").GetString().Should().Be("anthropic");
        r.GetProperty("model").GetString().Should().Be("some-model");
        r.GetProperty("user").GetString().Should().NotBeNullOrEmpty();
        r.TryGetProperty("grantedUtc", out _).Should().BeTrue();
        Directory.Exists(Path.Combine(Case(), ".ezassistant")).Should().BeFalse("nothing is written into the case");
    }

    [Fact]
    public void Consent_file_inside_the_case_folder_is_ignored()
    {
        Directory.CreateDirectory(Path.Combine(Case(), ".ezassistant"));
        File.WriteAllText(Path.Combine(Case(), ".ezassistant", "consent.json"),
            "{\"grantedUtc\":\"2026-01-01T00:00:00Z\",\"user\":\"x\",\"provider\":\"anthropic\",\"model\":\"m\"}");
        AssistantConsent.Has(Case(), "anthropic", null, Store).Should().BeFalse();
    }

    [Fact]
    public void Corrupt_store_means_no_consent()
    {
        Directory.CreateDirectory(Store);
        File.WriteAllText(Path.Combine(Store, AssistantConsent.StoreFileName), "{ not json");
        AssistantConsent.Has(Case(), "anthropic", null, Store).Should().BeFalse();
    }

    [Fact]
    public void Assistant_folder_is_case_level_and_hidden_in_the_tree_and_is_never_a_host()
    {
        CaseIndex.CaseLevelFolders.Should().Contain(".ezassistant");
        CaseTreeOrder.IsHiddenFolder(".ezassistant").Should().BeTrue();

        Directory.CreateDirectory(Path.Combine(_t.Path, ".ezassistant")); File.WriteAllText(Path.Combine(_t.Path, ".ezassistant", "consent.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_t.Path, ".ezassistant", "Network"));   // would look like a host with a category folder
        CaseIndex.Load(_t.Path).Hosts.Should().NotContain(".ezassistant");
    }

    [Fact]
    public void Assistant_folder_is_reserved_for_file_operations()
    {
        Directory.CreateDirectory(Path.Combine(_t.Path, ".ezassistant")); File.WriteAllText(Path.Combine(_t.Path, ".ezassistant", "consent.json"), "{}");
        var ops = new CaseFileOperations(_t.Path, (p, d) => { }, "tester", () => DateTime.UtcNow);
        var act = () => ops.CheckSource(Path.Combine(_t.Path, ".ezassistant", "consent.json"), "Delete");
        act.Should().Throw<CaseFileOperationException>();
    }
}
