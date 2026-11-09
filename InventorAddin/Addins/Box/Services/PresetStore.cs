using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using InventorAddin.Host;
using InventorAddin.Models;

namespace InventorAddin.Services;

public sealed record PresetLoadResult(IReadOnlyList<Preset> Presets, string? Warning);

/// <summary>
/// Đọc/ghi presets.json (xem mục 5b). File của người dùng nằm dưới %AppData%\InventorBoxAddin;
/// lần đầu chạy sao chép file mẫu đi kèm add-in sang đó. Không dùng BinaryFormatter.
/// </summary>
public sealed class PresetStore
{
    public const int SchemaVersion = InventorBoxGateway.SchemaVersion;
    public const string FileName = "presets.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class PresetDto
    {
        public string? Name { get; set; }
        public Dictionary<string, double>? Values { get; set; }
    }

    private sealed class FileDto
    {
        public int SchemaVersion { get; set; }
        public string Units { get; set; } = "mm";
        public List<PresetDto>? Presets { get; set; }
    }

    private readonly string _userFile;
    private readonly string _defaultFile;

    public PresetStore(string userDirectory, string defaultFile)
    {
        _userFile = Path.Combine(userDirectory, FileName);
        _defaultFile = defaultFile;
    }

    public string UserFile => _userFile;

    public PresetLoadResult Load()
    {
        try
        {
            if (!File.Exists(_userFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);
                if (File.Exists(_defaultFile)) File.Copy(_defaultFile, _userFile);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy the default preset file", ex);
        }

        var userResult = TryRead(_userFile, out var warning);
        if (userResult != null) return new PresetLoadResult(userResult, null);

        List<Preset>? fallback = File.Exists(_defaultFile) ? TryRead(_defaultFile, out _) : null;
        return new PresetLoadResult((IReadOnlyList<Preset>?)fallback ?? Array.Empty<Preset>(),
            warning ?? "Could not read the preset file; using the default presets.");
    }

    private static List<Preset>? TryRead(string path, out string? warning)
    {
        warning = null;
        try
        {
            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json);
            if (dto == null || dto.Presets == null)
            {
                warning = "The preset file is empty or has an invalid structure.";
                return null;
            }
            if (dto.SchemaVersion != SchemaVersion)
            {
                warning = $"The preset file has schemaVersion {dto.SchemaVersion}; the add-in only supports {SchemaVersion}.";
                return null;
            }

            var list = new List<Preset>();
            foreach (var p in dto.Presets)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var values = BoxParams.Complete(p.Values); // khóa lạ bỏ, thiếu thì mặc định
                var issues = BoxParams.Validate(values);
                list.Add(new Preset(p.Name.Trim(), values, issues.Count == 0 ? null : issues[0].Message));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {path}", ex);
            warning = "The preset file is corrupt or unreadable: " + ex.Message;
            return null;
        }
    }

    /// <summary>Ghi qua file tạm rồi đổi tên để không hỏng file nếu bị ngắt giữa chừng.</summary>
    public void Save(IEnumerable<Preset> presets)
    {
        var dto = new FileDto
        {
            SchemaVersion = SchemaVersion,
            Units = "mm",
            Presets = presets.Select(p => new PresetDto
            {
                Name = p.Name,
                Values = BoxParams.All.ToDictionary(d => d.Name, d => p.Values.TryGetValue(d.Name, out var v) ? v : d.Default),
            }).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);
        var tmp = _userFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json), new System.Text.UTF8Encoding(false));
        if (File.Exists(_userFile)) File.Replace(tmp, _userFile, null);
        else File.Move(tmp, _userFile);
    }
}

