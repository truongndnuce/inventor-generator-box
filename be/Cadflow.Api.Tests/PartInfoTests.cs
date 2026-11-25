using Cadflow.Api;
using Xunit;

namespace Cadflow.Api.Tests;

public class PartInfoTests
{
    private static Job Done(string product = "box", Dictionary<string, string>? texts = null) => new()
    {
        Id = "AJ-1",
        ProductCode = product,
        Status = JobStatus.Succeeded,
        RequestedBy = "an",
        TextInputs = texts ?? new(),
        Summary = { ["volumeCm3"] = 100, ["massKg"] = 0.5, ["bodies"] = 2 },
    };

    private static string? Value(Job j, string label) =>
        j.PartInfo.SelectMany(g => g.Rows).FirstOrDefault(r => r.Label == label)?.Value;

    [Fact]
    public void Nothing_until_the_job_has_succeeded()
    {
        var j = Done(); j.Status = JobStatus.Running;
        Assert.Empty(j.PartInfo);
    }

    [Fact]
    public void Material_and_finish_show_up_with_the_catalog_data()
    {
        var j = Done(texts: new() { ["PI_Material"] = "stainless-304", ["PI_Finish"] = "ral5010" });
        Assert.Contains("Stainless steel 304", Value(j, "Material"));
        Assert.Equal("EN 10088-2", Value(j, "Standard"));
        Assert.Contains("8 g/cm3", Value(j, "Density"));
        var look = j.PartInfo.SelectMany(g => g.Rows).First(r => r.Label == "Appearance");
        Assert.Equal("#0E4C92", look.Color);
        Assert.Contains("Stainless steel 304", Value(j, "Description"));
    }

    [Fact]
    public void Without_a_choice_the_rows_say_not_specified()
    {
        var j = Done();
        Assert.Equal("Not specified", Value(j, "Material"));
        Assert.Equal("Not specified", Value(j, "Appearance"));
    }

    [Fact]
    public void Part_number_follows_one_rule()
    {
        Assert.Equal("BOX-AJ-1", Value(Done(), "Part number"));
    }

    [Fact]
    public void Mass_from_the_model_wins_over_the_estimate()
    {
        var j = Done(texts: new() { ["PI_Material"] = "alu-6061" });
        Assert.Equal("0.5 kg", Value(j, "Mass (from the model)"));
        Assert.Null(Value(j, "Mass (volume x density)"));
    }

    [Fact]
    public void Catalog_check_rejects_unknown_codes_and_pick_keeps_only_part_information()
    {
        Assert.NotNull(MaterialCatalog.Check(new Dictionary<string, string> { ["PI_Material"] = "wood" }));
        Assert.NotNull(MaterialCatalog.Check(new Dictionary<string, string> { ["PI_Finish"] = "pink" }));
        Assert.Null(MaterialCatalog.Check(new Dictionary<string, string> { ["PI_Material"] = "abs", ["PI_Finish"] = "natural" }));
        var picked = MaterialCatalog.Pick(new Dictionary<string, string> { ["PI_Material"] = " abs ", ["Other"] = "x", ["PI_Finish"] = "" });
        Assert.Single(picked);
        Assert.Equal("abs", picked["PI_Material"]);
    }
}
