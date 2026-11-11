using System.Text;
using System.Text.RegularExpressions;
using InventorAddin.Gea;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

public sealed class DesignBlockTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static DesignModel Fixture(string name) =>
        DesignJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designer", name + ".design.json")))!;

    private static DesignModel Fan() => Fixture("all-types");

    private static string Body(DesignModel m) => DesignGenerator.Generate(m).Text;

    private static string WithBlock(DesignModel m) => DesignBlock.Append(Body(m), m);

    private static string Json(DesignModel m) => DesignJson.Serialize(m);

    private static string ModelLine(string text) => text.Split('\n').Single(l => l.StartsWith("!@MODEL ", StringComparison.Ordinal));

    // ------------------------------------------------------------------ write and read back

    [Fact]
    public void Generate_then_read_back_gives_an_equal_model_and_an_unchanged_body()
    {
        var m = Fan();
        var back = DesignBlock.ReadBack(WithBlock(m));

        Assert.True(back.HasModel, back.Reason);
        Assert.Equal(Json(m), Json(back.Model!));
        Assert.False(back.BodyChanged);
        Assert.Null(back.Reason);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("all-types")]
    [InlineData("box-form")]
    public void Round_trip_works_for_every_fixture(string fixture)
    {
        var m = Fixture(fixture);
        var back = DesignBlock.ReadBack(WithBlock(m));
        Assert.True(back.HasModel, back.Reason);
        Assert.Equal(Json(m), Json(back.Model!));
        Assert.False(back.BodyChanged);
    }

    [Fact]
    public void The_block_has_the_documented_shape()
    {
        var text = WithBlock(Fan());
        var lines = text.Split('\n');
        int h = Array.FindIndex(lines, l => l.StartsWith("! CADFlow Designer:", StringComparison.Ordinal));

        Assert.True(h > 1);
        Assert.StartsWith("! ----", lines[h - 1]);
        Assert.Equal("!@DESIGNER 1", lines[h + 2]);
        Assert.Matches(@"^!@BODY_SHA256 [0-9a-f]{64}$", lines[h + 3]);
        Assert.StartsWith("!@MODEL ", lines[h + 4]);
        Assert.Equal("", lines[h + 5]);                    // the file ends with exactly one newline
        Assert.Equal(h + 6, lines.Length);
        Assert.False(text.Contains('\r'));
    }

    [Fact]
    public void The_model_line_is_one_ascii_line_of_base64url()
    {
        var m = Fan();
        m.Groups[0].Params[0].Label = "Đường kính vỏ";       // non-ASCII text must survive
        var line = ModelLine(WithBlock(m));
        var payload = line["!@MODEL ".Length..];

        Assert.Matches("^[A-Za-z0-9_-]+$", payload);       // no '+', '/', '=' and no spaces
        Assert.All(line, c => Assert.True(c < 128));

        var back = DesignBlock.ReadBack(WithBlock(m));
        Assert.Equal("Đường kính vỏ", back.Model!.Groups[0].Params[0].Label);
    }

    [Fact]
    public void The_block_is_only_comments_so_the_compiler_and_the_form_are_unchanged()
    {
        var m = Fan();
        var plain = GeaScript.Compile(Body(m), GeaLibraries.Resolve);
        var withBlock = GeaScript.Compile(WithBlock(m), GeaLibraries.Resolve);

        Assert.False(withBlock.HasErrors, string.Join("\n", withBlock.Diagnostics));
        Assert.Equal(plain.EvaluateForm(null).Fields.Select(f => f.Name), withBlock.EvaluateForm(null).Fields.Select(f => f.Name));
        Assert.Equal(plain.Parameters.Select(p => p.Name), withBlock.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Strip_returns_the_body_and_append_is_idempotent()
    {
        var m = Fan();
        var body = Body(m);
        var withBlock = DesignBlock.Append(body, m);

        Assert.Equal(body, DesignBlock.Strip(withBlock));
        Assert.Equal(withBlock, DesignBlock.Append(DesignBlock.Strip(withBlock), m));
        Assert.Equal(withBlock, DesignBlock.Append(withBlock, m));       // appending to a file that already has a block replaces it
    }

    [Fact]
    public void Strip_of_a_file_without_a_block_only_normalizes()
    {
        Assert.Equal("A\nB\n", DesignBlock.Strip("A\r\nB\r\n\r\n\r\n"));
    }

    [Fact]
    public void Windows_line_endings_do_not_matter()
    {
        var m = Fan();
        var crlf = WithBlock(m).Replace("\n", "\r\n");
        var back = DesignBlock.ReadBack(crlf);

        Assert.True(back.HasModel, back.Reason);
        Assert.False(back.BodyChanged);
        Assert.Equal(Json(m), Json(back.Model!));
    }

    [Fact]
    public void Trailing_blank_lines_and_spaces_before_the_block_do_not_count_as_an_edit()
    {
        var m = Fan();
        var text = WithBlock(m);
        int cut = text.IndexOf("! ----", StringComparison.Ordinal);
        var edited = text[..cut].TrimEnd() + "   \n\n\n\n" + text[cut..];

        Assert.False(DesignBlock.ReadBack(edited).BodyChanged);
    }

    // ------------------------------------------------------------------ detecting hand edits

    [Fact]
    public void A_changed_character_in_the_body_is_detected_but_the_model_is_still_returned()
    {
        var m = Fan();
        var text = WithBlock(m).Replace("MIN 400", "MIN 401");
        var back = DesignBlock.ReadBack(text);

        Assert.True(back.HasModel);
        Assert.True(back.BodyChanged);
        Assert.Equal(Json(m), Json(back.Model!));
    }

    [Fact]
    public void A_statement_added_after_the_block_is_detected()
    {
        var text = WithBlock(Fan()) + "\nBEGIN_BUILD\nREGEN_MDL\nEND_BUILD\n";
        var back = DesignBlock.ReadBack(text);
        Assert.True(back.HasModel);
        Assert.True(back.BodyChanged);
    }

    [Fact]
    public void A_missing_or_malformed_hash_counts_as_an_edit()
    {
        var text = WithBlock(Fan());
        var noHash = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("!@BODY_SHA256")));
        var badHash = Regex.Replace(text, "!@BODY_SHA256 [0-9a-f]{64}", "!@BODY_SHA256 nothex");

        foreach (var t in new[] { noHash, badHash })
        {
            var back = DesignBlock.ReadBack(t);
            Assert.True(back.HasModel);
            Assert.True(back.BodyChanged);
        }
    }

    // ------------------------------------------------------------------ files that are not Designer files, and broken blocks

    [Fact]
    public void A_file_without_a_block_is_reported_as_text_only()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "box.gea"));
        var back = DesignBlock.ReadBack(text);

        Assert.False(back.HasModel);
        Assert.Null(back.Model);
        Assert.False(back.BodyChanged);
        Assert.Contains("not made with the Designer", back.Reason!);
    }

    [Fact]
    public void An_empty_text_is_reported_as_text_only()
    {
        Assert.False(DesignBlock.ReadBack("").HasModel);
        Assert.False(DesignBlock.ReadBack("\n\n").HasModel);
    }

    [Fact]
    public void Broken_blocks_never_throw_and_always_give_a_reason()
    {
        var good = WithBlock(Fan());
        string Mod(string from, string to) => good.Replace(from, to);
        var payload = ModelLine(good)["!@MODEL ".Length..];

        var cases = new Dictionary<string, string>
        {
            ["wrong version"] = Mod("!@DESIGNER 1", "!@DESIGNER 2"),
            ["no version"] = Mod("!@DESIGNER 1", "!@DESIGNER"),
            ["not a number"] = Mod("!@DESIGNER 1", "!@DESIGNER x"),
            ["broken base64"] = Mod("!@MODEL " + payload, "!@MODEL ***not base64***"),
            ["truncated payload"] = Mod("!@MODEL " + payload, "!@MODEL " + payload[..(payload.Length / 2)]),
            ["json array"] = Mod("!@MODEL " + payload, "!@MODEL " + Convert.ToBase64String(Encoding.UTF8.GetBytes("[1,2]")).TrimEnd('=').Replace('+', '-').Replace('/', '_')),
            ["json null"] = Mod("!@MODEL " + payload, "!@MODEL " + Convert.ToBase64String(Encoding.UTF8.GetBytes("null")).TrimEnd('=')),
            ["not json"] = Mod("!@MODEL " + payload, "!@MODEL " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")).TrimEnd('=')),
            ["no model line"] = string.Join("\n", good.Split('\n').Where(l => !l.StartsWith("!@MODEL"))),
            ["empty model line"] = Mod("!@MODEL " + payload, "!@MODEL "),
        };

        foreach (var (name, text) in cases)
        {
            var back = DesignBlock.ReadBack(text);
            Assert.False(back.HasModel, name);
            Assert.Null(back.Model);
            Assert.False(string.IsNullOrWhiteSpace(back.Reason), name);
        }
    }

    [Fact]
    public void A_model_larger_than_the_limit_is_refused_when_reading()
    {
        var huge = Convert.ToBase64String(new byte[DesignValidator.MaxModelBytes + 10]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var text = Body(Fan()) + "\n! ----\n! CADFlow Designer: x\n!@DESIGNER 1\n!@BODY_SHA256 " + new string('0', 64) + "\n!@MODEL " + huge + "\n";

        var back = DesignBlock.ReadBack(text);
        Assert.False(back.HasModel);
        Assert.Contains("256 KB", back.Reason!);
    }

    [Fact]
    public void Text_typed_in_the_build_cannot_pose_as_the_block()
    {
        // Raw build lines are always indented, and only a line that starts in column 0 counts. The model is the one that was generated.
        var m = Fan();
        m.Steps[0] = new DesignStep { Kind = "raw", Text = "!@DESIGNER 1\n!@BODY_SHA256 " + new string('a', 64) + "\n!@MODEL AAAA\nREGEN_MDL" };
        var back = DesignBlock.ReadBack(WithBlock(m));

        Assert.True(back.HasModel, back.Reason);
        Assert.False(back.BodyChanged);
        Assert.Equal(Json(m), Json(back.Model!));
    }

    [Fact]
    public void When_a_file_has_two_blocks_the_last_one_wins()
    {
        var first = Fixture("minimal");
        var second = Fan();
        var text = WithBlock(first) + "\n" + WithBlock(second);          // an accident, but it must not crash or mix them up
        var back = DesignBlock.ReadBack(text);
        Assert.True(back.HasModel, back.Reason);
        Assert.Equal(Json(second), Json(back.Model!));
        Assert.True(back.BodyChanged);                                    // the text before the last block is not the second model's body
    }

    // ------------------------------------------------------------------ interplay with the export / import of CADFlow

    private static Job ExportJob() => new()
    {
        Id = "AJ-20261111-000000-abcd",
        ProductCode = "imported",
        ProductVersion = 1,
        Inputs = new() { ["FN_Diameter"] = 1500, ["FN_Blades"] = 8 },
        TextInputs = new() { ["FN_Tag"] = "Fan \"B\"" },
    };

    [Fact]
    public void Exporting_a_job_keeps_the_Designer_block_and_the_CADFlow_value_block()
    {
        var m = Fan();
        var exported = GeaExport.Build(ExportJob(), WithBlock(m));

        Assert.Contains("!@DESIGNER 1", exported);
        Assert.Contains("!@CADFLOW 1", exported);
        Assert.Contains("!@VALUE FN_Diameter 1500", exported);

        var block = GeaExport.ParseBlock(exported);
        Assert.NotNull(block);
        Assert.Equal(1500, block!.Values["FN_Diameter"]);
        Assert.Equal("Fan \"B\"", block.Texts["FN_Tag"]);

        var back = DesignBlock.ReadBack(exported);
        Assert.True(back.HasModel, back.Reason);
        Assert.False(back.BodyChanged);                                   // the export block after ours is not an edit
        Assert.Equal(Json(m), Json(back.Model!));
    }

    [Fact]
    public void Exporting_twice_does_not_double_any_block()
    {
        var once = GeaExport.Build(ExportJob(), WithBlock(Fan()));
        var twice = GeaExport.Build(ExportJob(), once);

        Assert.Equal(once, twice);
        Assert.Single(twice.Split('\n'), l => l.StartsWith("!@DESIGNER"));
        Assert.Single(twice.Split('\n'), l => l.StartsWith("!@CADFLOW"));
    }

    [Fact]
    public void Strip_of_the_export_helper_removes_stray_marker_lines_but_keeps_the_Designer_block()
    {
        var text = WithBlock(Fan()) + "\n!@VALUE FN_Diameter 1\n";
        var stripped = GeaExport.StripBlock(text);

        Assert.Contains("!@DESIGNER 1", stripped);
        Assert.Contains("!@MODEL ", stripped);
        Assert.DoesNotContain("!@VALUE", stripped);
    }

    [Fact]
    public void A_Designer_file_imports_with_the_same_form_and_so_does_its_export()
    {
        var store = _db.NewStore();
        var jobs = new JobService(store, new ScriptStore(_db.DataDir), new ConfigStore(_db.DataDir), NullLogger<JobService>.Instance);
        var m = Fan();

        static System.Text.Json.JsonElement Form(object body) =>
            System.Text.Json.JsonSerializer.SerializeToElement(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).GetProperty("form");

        var plain = jobs.Import(Body(m));
        var designer = jobs.Import(WithBlock(m));
        var exported = jobs.Import(GeaExport.Build(ExportJob(), WithBlock(m)));

        Assert.Equal(200, plain.Status);
        Assert.Equal(200, designer.Status);
        Assert.Equal(200, exported.Status);

        var names = (System.Text.Json.JsonElement f) => f.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(names(Form(plain.Body!)), names(Form(designer.Body!)));
        Assert.Equal(names(Form(plain.Body!)), names(Form(exported.Body!)));
    }
}
