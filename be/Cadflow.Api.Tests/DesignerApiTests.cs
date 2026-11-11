using System.Text.Json;

namespace Cadflow.Api.Tests;

public class DesignerApiTests
{
    private static DesignModel Fixture(string name) =>
        DesignJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designer", name + ".design.json")))!;

    private static string Req(object o) => JsonSerializer.Serialize(o, DesignJson.Options);

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Generate_returns_text_with_the_block_that_parse_reads_back()
    {
        var g = DesignerApi.Generate(Req(new { model = Fixture("box-form") }));
        Assert.Equal(200, g.Status);
        var gea = Json(g.Body).GetProperty("gea").GetString()!;
        Assert.Contains("!@DESIGNER 1", gea);

        var p = DesignerApi.Parse(Req(new { text = gea }));
        var j = Json(p.Body);
        Assert.True(j.GetProperty("hasModel").GetBoolean());
        Assert.False(j.GetProperty("bodyChanged").GetBoolean());
    }

    [Fact]
    public void Model_problems_are_data_in_a_200_answer_and_gea_is_null()
    {
        var m = Fixture("box-form");
        m.Groups[0].Params[0].Name = "Length";
        var g = DesignerApi.Generate(Req(new { model = m }));
        Assert.Equal(200, g.Status);
        var j = Json(g.Body);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("gea").ValueKind);
        Assert.True(j.GetProperty("issues").GetArrayLength() > 0);
    }

    [Fact]
    public void A_missing_model_is_an_issue_not_an_error_status()
    {
        var g = DesignerApi.Generate("{}");
        Assert.Equal(200, g.Status);
        Assert.True(Json(g.Body).GetProperty("issues").GetArrayLength() > 0);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    [InlineData("null")]
    public void A_body_that_is_not_json_is_400(string body)
    {
        Assert.Equal(400, DesignerApi.Generate(body).Status);
        Assert.Equal(400, DesignerApi.Parse(body).Status);
        Assert.Equal(400, DesignerApi.DryRun(body).Status);
    }

    [Fact]
    public void A_model_of_the_wrong_shape_is_400()
    {
        const string body = "{\"model\":{\"version\":\"x\"}}";
        Assert.Equal(400, DesignerApi.Generate(body).Status);
        Assert.Equal(400, DesignerApi.DryRun(body).Status);
    }

    [Fact]
    public void A_generated_file_over_the_script_limit_is_reported_as_an_issue()
    {
        var m = Fixture("minimal");
        // fits the model cap (256 KB) but, with the base64 block (4/3 of the model) added to the body, the file passes 256 KB
        m.Steps.Add(new DesignStep { Kind = "raw", Text = string.Join("\n", Enumerable.Repeat("REGEN_MDL", 9000)) });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = string.Join("\n", Enumerable.Repeat("REGEN_MDL", 9000)) });
        var g = DesignerApi.Generate(Req(new { model = m }));
        var j = Json(g.Body);
        if (j.GetProperty("gea").ValueKind == JsonValueKind.Null)
            Assert.Contains(j.GetProperty("issues").EnumerateArray(), i => i.GetProperty("message").GetString()!.Contains("larger than 256 KB"));
        else
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(j.GetProperty("gea").GetString()!) <= GeaForms.MaxScriptBytes);
    }

    [Fact]
    public void Parse_refuses_a_text_over_the_script_limit_with_413()
    {
        Assert.Equal(413, DesignerApi.Parse(Req(new { text = new string('a', GeaForms.MaxScriptBytes + 1) })).Status);
    }

    [Fact]
    public async Task A_body_over_the_request_limit_is_413()
    {
        var big = new MemoryStream(new byte[DesignerApi.MaxRequestBytes + 1]);
        var (body, err) = await DesignerApi.ReadBody(big, null);          // no Content-Length: counted while reading
        Assert.Null(body);
        Assert.Equal(413, err!.Status);

        var (b2, e2) = await DesignerApi.ReadBody(new MemoryStream(), DesignerApi.MaxRequestBytes + 1);   // refused from the header alone
        Assert.Null(b2);
        Assert.Equal(413, e2!.Status);

        var (b3, e3) = await DesignerApi.ReadBody(new MemoryStream("{}"u8.ToArray()), 2);
        Assert.Equal("{}", b3);
        Assert.Null(e3);
    }

    [Fact]
    public void Dryrun_returns_the_runner_result()
    {
        var r = DesignerApi.DryRun(Req(new { model = Fixture("box-form"), values = new Dictionary<string, double> { ["BX_Length"] = 150 } }));
        Assert.Equal(200, r.Status);
        Assert.True(Json(r.Body).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void Builders_lists_the_catalog()
    {
        Assert.Equal(BuilderCatalogDto.List().Count, Json(DesignerApi.Builders().Body).GetArrayLength());
    }
}
