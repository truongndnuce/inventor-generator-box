using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class BuilderCatalogTests
{
    [Fact]
    public void One_entry_per_builder_with_the_same_header()
    {
        var list = BuilderCatalogDto.List();
        Assert.Equal(GeaBuilderCatalog.All.Count, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var b = GeaBuilderCatalog.All[i];
            Assert.Equal((b.Name, b.Version, b.Target, b.Description), (list[i].Name, list[i].Version, list[i].Target, list[i].Description));
        }
    }

    [Fact]
    public void Inputs_and_outputs_keep_their_order_and_details()
    {
        var list = BuilderCatalogDto.List();
        for (int i = 0; i < list.Count; i++)
        {
            var b = GeaBuilderCatalog.All[i];
            Assert.Equal(b.Inputs.Select(p => p.Name), list[i].Inputs.Select(p => p.Name));
            Assert.Equal(b.Outputs.Select(p => p.Name), list[i].Outputs.Select(p => p.Name));
            for (int j = 0; j < b.Inputs.Count; j++)
            {
                var (p, d) = (b.Inputs[j], list[i].Inputs[j]);
                Assert.Equal(GeaUnits.TypeName(p.Type), d.Type);
                Assert.Equal(p.Required, d.Required);
                Assert.Equal(p.Description, d.Description);
                Assert.Equal(p.Default?.ToString(), d.Default);
            }
        }
    }

    [Fact]
    public void Required_inputs_have_no_default_and_optional_ones_show_theirs()
    {
        var tank = BuilderCatalogDto.List().Single(b => b.Name == "Tank");
        Assert.Null(tank.Inputs.Single(p => p.Name == "Length").Default);
        Assert.NotNull(tank.Inputs.Single(p => p.Name == "SkirtHeight").Default);
        Assert.Equal("REFERENCE", BuilderCatalogDto.List().Single(b => b.Name == "Cover").Inputs.Single(p => p.Name == "Tank").Type);
    }

    [Fact]
    public void The_six_builders_are_listed()
    {
        Assert.Equal(new[] { "Tank", "Cover", "Block", "Cylinder", "Bushing", "Radiator" }.OrderBy(x => x),
                     BuilderCatalogDto.List().Select(b => b.Name).OrderBy(x => x));
    }

    [Fact]
    public void The_list_serializes_to_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(BuilderCatalogDto.List(), DesignJson.Options);
        Assert.Contains("\"inputs\"", json);
        Assert.Contains("\"Tank\"", json);
    }
}
