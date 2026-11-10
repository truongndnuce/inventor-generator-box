using System.Text;
using System.Text.RegularExpressions;

namespace Cadflow.Api;

/// <summary>
/// Thư viện component cho <c>INCLUDE lib:&lt;tên&gt;</c>: các file <c>Scripts/lib/&lt;tên&gt;.gea</c> đi kèm server.
/// Server mở rộng INCLUDE trước khi lưu script, nên worker Inventor chỉ nhận script đã mở rộng và không cần thư viện.
/// </summary>
public static partial class GeaLibraries
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Scripts", "lib");

    /// <summary>Tên các thư viện đi kèm (file <c>Scripts/lib/*.gea</c> có tên hợp lệ), sắp xếp.</summary>
    public static IEnumerable<string> Names() =>
        Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*.gea").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => ValidName().IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal)
            : [];

    /// <summary>Nội dung thư viện hoặc null nếu tên không hợp lệ hoặc không có file (không bao giờ ra ngoài thư mục lib).</summary>
    public static string? Resolve(string name) =>
        ValidName().IsMatch(name) && Path.Combine(Dir, name + ".gea") is var path && File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : null;
}
