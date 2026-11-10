using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace InventorAddin.Gea;

/// <summary>
/// Nơi nạp thư viện cho <c>INCLUDE lib:&lt;tên&gt;</c> khi script nằm trên đĩa (add-in Inventor).
/// Thứ tự tìm: <c>&lt;thư mục file&gt;/lib/&lt;tên&gt;.gea</c>, <c>&lt;thư mục file&gt;/&lt;tên&gt;.gea</c>, rồi thư mục thư viện đi kèm add-in.
/// Tên không hợp lệ (có đường dẫn, <c>..</c>, ký tự lạ) không bao giờ chạm tới đĩa.
/// </summary>
public static class GeaLibraryResolver
{
    private static readonly Regex ValidName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static Func<string, string?> For(string? scriptDirectory, string bundledLibDirectory) => name =>
    {
        if (!ValidName.IsMatch(name)) return null;
        var file = name + ".gea";
        if (!string.IsNullOrEmpty(scriptDirectory))
        {
            var beside = Read(Path.Combine(scriptDirectory, "lib", file)) ?? Read(Path.Combine(scriptDirectory, file));
            if (beside != null) return beside;
        }
        return Read(Path.Combine(bundledLibDirectory, file));
    };

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
