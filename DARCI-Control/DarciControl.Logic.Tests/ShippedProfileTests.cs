#nullable enable

using System.Text.Json;
using DarciControl.Logic.Nodes;
using DarciControl.Logic.Packaging;

namespace DarciControl.Logic.Tests;

/// <summary>
/// What a distributable's model profile is allowed to be.
///
/// <para>The rule: a recipient has no access to this machine's private endpoints, so the shipped profile
/// must need NO credentials. A profile naming an <c>api_key_env</c> would hand a collaborator a core that
/// refuses to start over a secret they can never obtain — and it would fail at startup, by name, which
/// looks exactly like a broken build rather than a missing opt-in.</para>
///
/// <para>These read the REAL repo files, because the thing being protected is a property of what is
/// committed, not of a fixture.</para>
/// </summary>
public sealed class ShippedProfileTests
{
    private static string RepoRoot() => RequiredModelsTests.FindRepoRoot();

    [Fact]
    public void TheCommittedDefaultProfile_NamesNoApiKeyEnv()
    {
        // The single assertion the collaborator story rests on.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "DARCI-v4", "host-profile.json"));

        Assert.DoesNotContain("api_key_env", json);
        Assert.DoesNotContain("sk-", json);
    }

    [Fact]
    public void TheZipShipsTheDefaultProfile_AndNeverAnAlternateOne()
    {
        var request = new ZipBuildRequest
        {
            RepoRoot = RepoRoot(),
            OutputPath = Path.Combine(Path.GetTempPath(), $"profile-check-{Guid.NewGuid():N}.zip"),
        };

        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), Path.GetTempPath());
        var profileEntries = plan.Entries.Where(e => e.EntryPath.Contains("host-profile")).ToList();

        var shipped = Assert.Single(profileEntries);
        Assert.Equal("darci/host-profile.json", shipped.EntryPath);

        // A profile selected per-machine via DARCI_HOST_PROFILE must not travel.
        Assert.DoesNotContain(plan.Entries, e => e.EntryPath.Contains("hybrid"));
        Assert.DoesNotContain(plan.Entries, e => e.EntryPath.Contains("claude"));
    }

    [Fact]
    public void TheProfileTheZipShips_RequiresNoCredentials()
    {
        // Reads the file the plan actually selected, rather than assuming which one that is.
        var request = new ZipBuildRequest
        {
            RepoRoot = RepoRoot(),
            OutputPath = Path.Combine(Path.GetTempPath(), $"profile-check-{Guid.NewGuid():N}.zip"),
        };

        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), Path.GetTempPath());
        var shipped = plan.Entries.Single(e => e.EntryPath == "darci/host-profile.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(shipped.Source));
        var root = doc.RootElement;

        foreach (var provider in root.GetProperty("providers").EnumerateObject())
        {
            Assert.False(provider.Value.TryGetProperty("api_key_env", out _),
                $"provider '{provider.Name}' in the SHIPPED profile needs a key; a recipient cannot supply one.");
        }

        // And every class still binds, so the recipient's broker starts rather than failing on an
        // unsatisfiable class.
        var classes = root.GetProperty("classes");
        Assert.True(classes.EnumerateObject().Any());
        foreach (var cls in classes.EnumerateObject())
            Assert.False(string.IsNullOrWhiteSpace(cls.Value.GetProperty("model").GetString()));
    }

    [Fact]
    public void TheHybridProfileExists_ButIsOptIn()
    {
        // The remote routing still has to be available to this machine — it is simply not the default,
        // and not shipped. If this file disappears, the remote tiers quietly stop being reachable.
        var hybrid = Path.Combine(RepoRoot(), "DARCI-v4", "host-profile.hybrid.json");
        Assert.True(File.Exists(hybrid), "the opt-in hybrid profile should remain in the repo");

        var json = File.ReadAllText(hybrid);
        Assert.Contains("api_key_env", json);   // it names the var...
        Assert.DoesNotContain("sk-", json);     // ...and never carries the secret
    }
}
