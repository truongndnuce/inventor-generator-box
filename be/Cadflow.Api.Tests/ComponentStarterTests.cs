using InventorAddin.Gea;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

public sealed class ComponentStarterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ComponentCatalog _catalog = new();

    public void Dispose() => _db.Dispose();

    private string Starter(string name = "tank-base-cover", string? productName = null, string? prefix = null)
    {
        var r = _catalog.Starter(name, productName, prefix);
        Assert.True(r.Text != null, $"{r.Status} {r.Error}");
        return r.Text!;
    }

    [Fact]
    public void Default_starter_compiles_without_errors_and_includes_the_library_in_form_and_build()
    {
        var text = Starter();
        var script = GeaScript.Compile(text, GeaLibraries.Resolve);

        Assert.False(script.HasErrors, string.Join("\n", script.Diagnostics));
        Assert.Equal(2, text.Split('\n').Count(l => l.Trim() == "INCLUDE lib:tank-base-cover"));
        Assert.Contains("PARAM_PREFIX MY_", text);
        Assert.Equal("New product", script.Program.ProductName);
        Assert.Contains(script.Parameters, p => p.Name == "TR_TankLength");
    }

    [Fact]
    public void Custom_product_name_and_prefix_are_applied()
    {
        var text = Starter(productName: "Reactor vessel", prefix: "RX_");
        var script = GeaScript.Compile(text, GeaLibraries.Resolve);

        Assert.False(script.HasErrors, string.Join("\n", script.Diagnostics));
        Assert.Equal("Reactor vessel", script.Program.ProductName);
        Assert.Equal("RX_", script.Program.ParamPrefix);
        Assert.Contains("RX_Example", text);          // ví dụ trong chú thích dùng tiền tố mới
    }

    [Fact]
    public void Starter_can_be_imported_and_ordered()
    {
        var store = _db.NewStore();
        var jobs = new JobService(store, new ScriptStore(_db.DataDir), new ConfigStore(_db.DataDir), NullLogger<JobService>.Instance);

        var import = jobs.Import(Starter());
        Assert.Equal(200, import.Status);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(import.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("TR_TankLength", json.GetProperty("form").ToString());

        var order = jobs.Create(new CreateJobRequest(null, new() { ["TR_TankLength"] = 2400 }, "tester", ScriptId: json.GetProperty("scriptId").GetString()));
        Assert.True(order.Ok, order.Error?.ToString());
        Assert.Equal(JobStatus.Queued, store.Get(order.Job!.Id)!.Status);
    }

    [Fact]
    public void Every_step_is_explained_with_comments()
    {
        var text = Starter();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        // Mỗi lệnh khai báo ở phần đầu có chú thích riêng ngay phía trên.
        foreach (var keyword in new[] { "GEA_VERSION", "PRODUCT_NAME", "PARAM_PREFIX", "TARGET" })
        {
            var i = Array.FindIndex(lines, l => l.StartsWith(keyword + " "));
            Assert.True(i > 0 && ComponentCommentsTests.IsComment(lines[i - 1]), $"Lệnh {keyword} chưa có chú thích ngay phía trên.");
        }
        // Mỗi INCLUDE có chú thích nói nó chèn gì; có chỗ thêm tham số và bước dựng riêng.
        foreach (var i in Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == "INCLUDE lib:tank-base-cover"))
            Assert.True(ComponentCommentsTests.IsComment(lines[i - 1]), "INCLUDE chưa có chú thích.");
        Assert.Contains(lines, l => l.Contains("thông số của riêng bạn"));
        Assert.Contains(lines, l => l.Contains("bước dựng của riêng bạn"));
        // Liệt kê tham số của component và các biến dùng chung.
        Assert.Contains("TR_TankLength", text);
        Assert.Contains("hTop", text);
        // Quy ước chung cho mọi đoạn lệnh trong FORM/BUILD.
        Assert.Empty(ComponentCommentsTests.UncommentedParagraphs("starter", lines));
    }

    [Theory]
    [InlineData("a\"b", "MY_")]
    [InlineData("a\\b", "MY_")]
    [InlineData("two\nlines", "MY_")]
    [InlineData("ok", "my")]          // thiếu dấu _ ở cuối
    [InlineData("ok", "1X_")]
    [InlineData("ok", "TOO_LONG_PREFIX_NAME_")]
    [InlineData("ok", "A B_")]
    public void Invalid_input_is_rejected_with_400(string productName, string prefix)
    {
        var r = _catalog.Starter("tank-base-cover", productName, prefix);
        Assert.Equal(400, r.Status);
        Assert.Null(r.Text);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void Blank_product_name_or_prefix_falls_back_to_the_defaults()
    {
        var text = _catalog.Starter("tank-base-cover", "  ", "").Text!;
        Assert.Contains("PRODUCT_NAME \"New product\"", text);
        Assert.Contains("PARAM_PREFIX MY_", text);
    }

    [Fact]
    public void Name_longer_than_80_is_rejected()
    {
        Assert.Equal(400, _catalog.Starter("tank-base-cover", new string('x', 81), "MY_").Status);
        Assert.NotNull(_catalog.Starter("tank-base-cover", new string('x', 80), "MY_").Text);
    }

    [Fact]
    public void Unknown_library_is_404_and_broken_library_is_400_without_throwing()
    {
        Assert.Equal(404, _catalog.Starter("missing", null, null).Status);
        Assert.Equal(404, _catalog.Starter("../x", null, null).Status);

        var broken = new ComponentCatalog(() => ["bad"], _ => "GEA_VERSION 1\nPRODUCT_NAME \"Bad\"\nnot valid\n");
        var r = broken.Starter("bad", null, null);
        Assert.Equal(400, r.Status);
        Assert.Contains("errors", r.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
