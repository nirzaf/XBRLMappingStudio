using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Project.Tests;

public sealed class FunctionSignatureTests
{
    private static FunctionDefinition Definition() => new()
    {
        FunctionId = "typed", Name = "Typed", CatalogVersion = "1.0",
        Inputs = new() { new("text", PortShape.String, PortCardinality.Single), new("items", PortShape.Record, PortCardinality.Collection) },
        Outputs = new() { new("ok", PortShape.Boolean, PortCardinality.Single), new("count", PortShape.Integer, PortCardinality.Single) }
    };
    private static MappingProject Project() => new()
    {
        ProjectId = Id.New(), Name = "Test", ProfileId = "test", ProfileDigest = "test", FunctionCatalogVersion = "1.0"
    };

    [Fact]
    public void TypedMultiPortSignaturesRoundTripAndRevise()
    {
        var definition = Definition();
        var project = FunctionLibraryService.CreateFunction(Project(), definition);
        var revised = FunctionLibraryService.CreateRevision(project, "typed", 1, definition.Nodes, definition.Edges);
        var package = new FunctionPackageDocument { RootFunctionId = "typed", RootRevision = 2, Functions = revised.Functions };
        var restored = FunctionPackageSerializer.Deserialize(FunctionPackageSerializer.Serialize(package));
        Assert.Equal(definition.Inputs, restored.Functions[1].Inputs);
        Assert.Equal(definition.Outputs, restored.Functions[1].Outputs);
        var imported = FunctionLibraryService.ImportAs(Project(), restored.Functions, "typed", "copy");
        Assert.Equal(2, imported.Functions.Count);
        Assert.Equal(definition.Inputs, imported.Functions[1].Inputs);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("shape")]
    [InlineData("cardinality")]
    [InlineData("name")]
    [InlineData("outputs")]
    public void LibraryAndPackagesRejectMalformedSignatures(string corruption)
    {
        var definition = Definition();
        switch (corruption)
        {
            case "duplicate": definition.Inputs.Add(definition.Inputs[0]); break;
            case "shape": definition.Inputs[0] = definition.Inputs[0] with { Shape = (PortShape)999 }; break;
            case "cardinality": definition.Inputs[0] = definition.Inputs[0] with { Cardinality = (PortCardinality)999 }; break;
            case "name": definition.Inputs[0] = definition.Inputs[0] with { Name = " " }; break;
            case "outputs": definition.Outputs.Clear(); break;
        }
        Assert.Throws<InvalidOperationException>(() => FunctionLibraryService.CreateFunction(Project(), definition));
        Assert.Throws<InvalidOperationException>(() => FunctionLibraryService.ImportAs(Project(), new[] { definition }, "typed", "copy"));
        Assert.Throws<InvalidOperationException>(() => FunctionPackageSerializer.Serialize(new FunctionPackageDocument
        { RootFunctionId = "typed", RootRevision = 1, Functions = new() { definition } }));
        var project = FunctionLibraryService.CreateFunction(Project(), Definition());
        Assert.Throws<InvalidOperationException>(() => FunctionLibraryService.CreateRevision(project, "typed", 1,
            definition.Nodes, definition.Edges, inputs: definition.Inputs, outputs: definition.Outputs));
    }
}
