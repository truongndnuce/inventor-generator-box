using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InventorAddin.Host;

namespace InventorAddin.Services.Gea;

/// <summary>
/// Nơi đặt file .gea: mẫu đi kèm add-in (thư mục Scripts cạnh DLL, chỉ đọc) và bản của người dùng
/// (%AppData%\InventorBoxAddin\scripts). Mở một mẫu từ add-in thì sao chép sang thư mục người dùng để sửa.
/// </summary>
public static class GeaScriptLibrary
{
    public static string BundledDirectory { get; } =
        Path.Combine(Path.GetDirectoryName(typeof(GeaScriptLibrary).Assembly.Location) ?? ".", "Scripts");

    public static string UserDirectory => Path.Combine(Log.DataDirectory, "scripts");

    public static string BundledPath(string fileName) => Path.Combine(BundledDirectory, fileName);

    public static IReadOnlyList<string> BundledScripts()
    {
        try
        {
            return Directory.Exists(BundledDirectory)
                ? Directory.GetFiles(BundledDirectory, "*.gea").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Bản sửa được của một mẫu: sao chép vào thư mục người dùng nếu chưa có, trả về đường dẫn bản sao.</summary>
    public static string EditableCopy(string bundledPath)
    {
        Directory.CreateDirectory(UserDirectory);
        var target = Path.Combine(UserDirectory, Path.GetFileName(bundledPath));
        if (!File.Exists(target)) File.Copy(bundledPath, target);
        return target;
    }
}
