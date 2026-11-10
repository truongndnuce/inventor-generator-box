namespace Cadflow.Api.Tests;

/// <summary>
/// Quy ước cho thư viện component (Scripts/lib/*.gea): file tải về phải giải thích từng bước.
/// Đoạn đầu của chú thích đầu file là mô tả ngắn; mỗi đoạn lệnh (các dòng liên tiếp, không có dòng trống) trong khối FORM/BUILD có chú thích.
/// </summary>
public class ComponentCommentsTests
{
    public static IEnumerable<object[]> Libraries() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Scripts", "lib"), "*.gea")
            .Select(f => new object[] { Path.GetFileName(f) });

    private static string[] Lines(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "lib", name)).Replace("\r\n", "\n").Split('\n');

    private static bool IsComment(string l) => l.TrimStart().StartsWith('!');

    [Fact]
    public void There_is_at_least_one_library()
    {
        Assert.NotEmpty(Libraries());
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void File_starts_with_a_description(string file)
    {
        var header = Lines(file).TakeWhile(l => l.Trim().Length == 0 || IsComment(l)).ToArray();
        var text = header.Select(l => l.Trim().TrimStart('!').Trim()).Where(t => t.Length > 0 && !t.All(c => c == '=')).ToArray();
        Assert.True(text.Length > 0, $"{file}: thiếu chú thích mô tả ở đầu file.");
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void Every_statement_paragraph_in_FORM_and_BUILD_has_a_comment(string file)
    {
        var lines = Lines(file);
        var problems = new List<string>();
        string? block = null;
        var para = new List<(int No, string Text)>();

        void Flush()
        {
            if (block != null && para.Count > 0 && !para.Any(p => IsComment(p.Text)))
                problems.Add($"{file}:{para[0].No} ({block}): đoạn lệnh bắt đầu bằng '{para[0].Text.Trim()}' chưa có chú thích giải thích.");
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (block == null)
            {
                if (t == "BEGIN_FORM") block = "FORM";
                else if (t == "BEGIN_BUILD") block = "BUILD";
                continue;
            }
            if (t == "END_FORM" || t == "END_BUILD") { Flush(); block = null; continue; }
            if (t.Length == 0) { Flush(); continue; }
            para.Add((i + 1, lines[i]));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
