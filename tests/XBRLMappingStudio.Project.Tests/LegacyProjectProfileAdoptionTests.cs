using System.Text;
using System.Text.Json.Nodes;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Project.Tests;

public sealed class LegacyProjectProfileAdoptionTests
{
    [Fact]
    public void CreateDraft_RequiresExplicitIdentityAndClearsApprovalsInIndependentCopy()
    {
        var source = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("44444444-1111-2222-3333-444444444401"));
        source.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = source.SemanticRevision,
            SemanticDigest = source.ComputeSemanticDigest(),
            ApprovedBy = "legacy-reviewer",
            ApprovedUtc = DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
            ProfileDigest = source.ProfileDigest
        });
        var sourceJson = ProjectSerializer.Serialize(source);
        var legacy = JsonNode.Parse(sourceJson)!.AsObject();
        legacy.Remove("profileId");
        legacy.Remove("profileDigest");
        var originalBytes = Encoding.UTF8.GetBytes(legacy.ToJsonString(ProjectSerializer.Options));
        var before = originalBytes.ToArray();
        var selected = SyntheticProfile.Build();

        var draft = LegacyProjectProfileAdoption.CreateDraft(
            originalBytes, selected.ProfileId, selected.Digest);

        Assert.Equal(new[] { "profileId", "profileDigest" }, draft.AddedIdentityFields);
        Assert.Equal(selected.ProfileId, draft.Project.ProfileId);
        Assert.Equal(selected.Digest, draft.Project.ProfileDigest);
        Assert.NotEqual(source.ProjectId, draft.Project.ProjectId);
        Assert.Empty(draft.Project.Approvals);
        var originalProjectJson = JsonNode.Parse(ProjectSerializer.Serialize(source))!.AsObject();
        var adoptedProjectJson = JsonNode.Parse(ProjectSerializer.Serialize(draft.Project))!.AsObject();
        Assert.True(JsonNode.DeepEquals(originalProjectJson["nodes"], adoptedProjectJson["nodes"]));
        Assert.True(JsonNode.DeepEquals(originalProjectJson["edges"], adoptedProjectJson["edges"]));
        Assert.True(JsonNode.DeepEquals(originalProjectJson["bindings"], adoptedProjectJson["bindings"]));
        Assert.Equal(before, originalBytes);
    }

    [Fact]
    public void CreateDraft_FillsOnlyMissingIdentityThatMatchesExplicitSelection()
    {
        var source = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("44444444-1111-2222-3333-444444444402"));
        var legacy = JsonNode.Parse(ProjectSerializer.Serialize(source))!.AsObject();
        legacy.Remove("profileDigest");
        var selected = SyntheticProfile.Build();

        var draft = LegacyProjectProfileAdoption.CreateDraft(
            Encoding.UTF8.GetBytes(legacy.ToJsonString(ProjectSerializer.Options)),
            selected.ProfileId,
            selected.Digest);

        Assert.Equal(new[] { "profileDigest" }, draft.AddedIdentityFields);
        Assert.Equal(source.ProfileId, draft.Project.ProfileId);
        Assert.Equal(selected.Digest, draft.Project.ProfileDigest);
    }

    [Fact]
    public void CreateDraft_RefusesConflictsCompleteIdentityDuplicateFieldsAndUnsupportedData()
    {
        var source = TestFixtures.BuildRevenueMappingProject(
            idSeed: Guid.Parse("44444444-1111-2222-3333-444444444403"));
        var complete = Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(source));
        var selected = SyntheticProfile.Build();
        Assert.Throws<InvalidOperationException>(() =>
            LegacyProjectProfileAdoption.CreateDraft(complete, selected.ProfileId, selected.Digest));

        var legacy = JsonNode.Parse(ProjectSerializer.Serialize(source))!.AsObject();
        legacy.Remove("profileDigest");
        var conflicting = Encoding.UTF8.GetBytes(legacy.ToJsonString(ProjectSerializer.Options));
        Assert.Throws<InvalidOperationException>(() =>
            LegacyProjectProfileAdoption.CreateDraft(conflicting, "another-profile", selected.Digest));

        var duplicate = Encoding.UTF8.GetBytes(
            ProjectSerializer.Serialize(source).Replace("\"profileId\":", "\"profileId\":\"first\",\"profileId\":", StringComparison.Ordinal));
        var duplicateError = Assert.Throws<ProjectSchemaException>(() =>
            LegacyProjectProfileAdoption.CreateDraft(duplicate, selected.ProfileId, selected.Digest));
        Assert.Equal(DiagnosticCodes.PrjCorruptFile, duplicateError.Code);

        var unsupported = JsonNode.Parse(ProjectSerializer.Serialize(source))!.AsObject();
        unsupported.Remove("profileId");
        unsupported.Remove("profileDigest");
        unsupported["unknownExecutableExtension"] = new JsonObject { ["enabled"] = true };
        var unsupportedError = Assert.Throws<ProjectSchemaException>(() =>
            LegacyProjectProfileAdoption.CreateDraft(
                Encoding.UTF8.GetBytes(unsupported.ToJsonString(ProjectSerializer.Options)),
                selected.ProfileId,
                selected.Digest));
        Assert.Equal(DiagnosticCodes.PrjSchemaVersionUnsupported, unsupportedError.Code);
    }
}
