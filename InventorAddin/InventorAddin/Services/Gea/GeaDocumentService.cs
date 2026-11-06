using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using InventorAddin.Gea;
using InventorAddin.Host;
using InventorAddin.Models;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

public enum ScriptDocKind
{
    NoDocument,
    NotPart,
    EmptyPart,
    ForeignContent, // Part có hình học hoặc Parameter khác
    ScriptPart,     // Part đã được dựng bởi một script .gea (có script lưu kèm)
}

/// <summary>Script đã lưu trong Part ở lần Apply gần nhất: nội dung, đường dẫn file nguồn, giá trị form.</summary>
public sealed record StoredScript(string Text, string? Path, string Product, IReadOnlyDictionary<string, object?> Values, string? AppliedAt);

public sealed record ScriptDocState(ScriptDocKind Kind, string Message, string? DocKey, StoredScript? Stored);

/// <summary>Cổng của ViewModel Script tới Inventor. Mọi lời gọi chạy trên UI thread của Inventor.</summary>
public interface IScriptGateway
{
    ScriptDocState GetState();

    /// <summary>Dựng lại model theo script và giá trị form trong một Transaction (xem <see cref="GeaDocumentService"/>).</summary>
    OperationResult Apply(GeaScript script, GeaFormResult form, string? sourcePath, Action<string>? progress, bool allowForeignContent);

    /// <summary>Xóa mọi đối tượng do script tạo và bỏ liên kết script khỏi Part.</summary>
    OperationResult Remove();
}

/// <summary>
/// Áp dụng script .gea lên Part theo cách của SmartAssembly: mỗi lần Apply chạy lại toàn bộ BUILD.
/// <list type="number">
/// <item>Mở một Transaction (Ctrl+Z hoàn tác cả lần Apply).</item>
/// <item>Xóa mọi feature, sketch, work plane mang dấu sở hữu của script trong Part (lần dựng trước).</item>
/// <item>Đồng bộ thông số FORM thành User Parameter, chạy BUILD trên <see cref="InventorGeaHost"/>.</item>
/// <item>Xóa Parameter của script không còn khai báo; lưu script, đường dẫn và giá trị vào Part.</item>
/// <item>Cập nhật model; lỗi ở bất kỳ bước nào thì hủy Transaction, model trở về như trước khi Apply.</item>
/// </list>
/// Nhờ vậy sửa script (thêm, bớt, đổi bộ phận) rồi Apply thì model đổi theo đúng script mới,
/// còn đối tượng người dùng tự vẽ (không mang dấu sở hữu) được giữ nguyên.
/// </summary>
internal sealed class GeaDocumentService : IScriptGateway
{
    public const string ScriptSet = "GEA_Script";

    private readonly Inv.Application _app;

    public GeaDocumentService(Inv.Application app) => _app = app;

    // ---------------------------------------------------------------- trạng thái

    private Inv.PartDocument? ActivePart(out ScriptDocState? early)
    {
        early = null;
        Inv._Document? doc = null;
        try { doc = _app.ActiveDocument; } catch { /* không có document */ }
        if (doc == null)
        {
            early = new ScriptDocState(ScriptDocKind.NoDocument, "No document is open. Open or create a Part.", null, null);
            return null;
        }
        if (doc.DocumentType != Inv.DocumentTypeEnum.kPartDocumentObject)
        {
            early = new ScriptDocState(ScriptDocKind.NotPart, "Scripts with TARGET PART need an active Part (.ipt).", DocKey(doc), null);
            return null;
        }
        return (Inv.PartDocument)doc;
    }

    private static string? DocKey(Inv._Document doc)
    {
        try { return doc.InternalName + "|" + doc.FullDocumentName; }
        catch { return null; }
    }

    public ScriptDocState GetState()
    {
        try
        {
            var part = ActivePart(out var early);
            if (part == null) return early!;
            var key = DocKey((Inv._Document)part);

            var stored = ReadStored(part);
            if (stored != null)
                return new ScriptDocState(ScriptDocKind.ScriptPart,
                    $"This Part was built by the script \"{stored.Product}\"{(stored.AppliedAt != null ? $" (applied {stored.AppliedAt})" : "")}. Edit the script or values, then Apply.",
                    key, stored);

            var def = part.ComponentDefinition;
            bool foreign = def.SurfaceBodies.Count > 0 || def.Features.Count > 0 || def.Sketches.Count > 0
                           || def.Parameters.UserParameters.Count > 0;
            return foreign
                ? new ScriptDocState(ScriptDocKind.ForeignContent, "This Part already has other geometry or parameters. Applying a script adds to it.", key, null)
                : new ScriptDocState(ScriptDocKind.EmptyPart, "Empty Part. Open or write a script, then Apply to build the model.", key, null);
        }
        catch (Exception ex)
        {
            Log.Error("Script GetState failed", ex);
            return new ScriptDocState(ScriptDocKind.NoDocument, "Could not read the document state: " + ex.Message, null, null);
        }
    }

    public static StoredScript? ReadStored(Inv.PartDocument part)
    {
        try
        {
            if (!part.AttributeSets.NameIsUsed[ScriptSet]) return null;
            var set = part.AttributeSets[ScriptSet];
            string? Get(string k) => set.NameIsUsed[k] ? set[k].Value as string : null;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var json = Get("Values");
            if (!string.IsNullOrEmpty(json))
            {
                using var docJson = JsonDocument.Parse(json);
                foreach (var p in docJson.RootElement.EnumerateObject())
                    values[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.Number => p.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => p.Value.GetString(),
                        _ => null,
                    };
            }
            return new StoredScript(Get("Script") ?? "", Get("Path"), Get("Product") ?? "", values, Get("AppliedAt"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the stored script: " + ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- áp dụng

    public OperationResult Apply(GeaScript script, GeaFormResult form, string? sourcePath, Action<string>? progress, bool allowForeignContent)
    {
        var part = ActivePart(out var early);
        if (part == null) return new OperationResult(false, early!.Message);

        var state = GetState();
        if (state.Kind == ScriptDocKind.ForeignContent && !allowForeignContent)
            return new OperationResult(false, "Confirmation is required before applying a script to a Part that already has content.");
        return Apply(part, script, form, sourcePath, progress);
    }

    /// <summary>Áp dụng script lên một Part cụ thể (dùng cho web worker với Part mới tạo).</summary>
    public OperationResult Apply(Inv.PartDocument part, GeaScript script, GeaFormResult form, string? sourcePath, Action<string>? progress)
    {
        if (script.HasErrors)
        {
            var d = script.Diagnostics.First(x => x.Severity == GeaSeverity.Error);
            return new OperationResult(false, "The script has errors: " + d);
        }
        if (script.Program.Target != "PART")
            return new OperationResult(false, $"TARGET {script.Program.Target} is not supported by the Inventor host yet (only PART).");
        if (!form.Ok)
            return new OperationResult(false, "The form has errors: " + form.Errors[0].Message);

        var warnings = new List<string>();
        var log = new List<string>();
        var result = InventorTransactions.Run(_app, part, $"GEA: {Short(script.Program.ProductName)}", () =>
        {
            var binding = ReadBinding(part) ?? Guid.NewGuid().ToString("N");
            int removed = DeleteOwnedGeometry(part, binding);
            if (removed > 0) progress?.Invoke($"Removed {removed} objects from the previous build.");

            var host = new InventorGeaHost(_app, part, binding, msg =>
            {
                log.Add(msg);
                progress?.Invoke(msg);
            });
            var run = script.RunBuild(form, host);
            if (!run.Ok)
                throw new InvalidOperationException(run.Error!.Format(System.IO.Path.GetFileName(sourcePath ?? "script.gea")));
            host.RemoveStaleParameters();
            warnings.AddRange(host.Warnings);
            WriteBinding(part, binding, script, form, sourcePath);
        }, "Script applied.");

        if (!result.Ok) return result;
        var msg = $"Script applied: {Short(script.Program.ProductName)}.";
        if (log.Count > 0) msg += " " + log[^1];
        if (warnings.Count > 0)
        {
            foreach (var w in warnings) Log.Warn(w);
            msg += $" Warnings: {string.Join(" ", warnings)}";
        }
        return new OperationResult(true, msg);
    }

    public OperationResult Remove()
    {
        var part = ActivePart(out var early);
        if (part == null) return new OperationResult(false, early!.Message);
        var binding = ReadBinding(part);
        if (binding == null) return new OperationResult(false, "This Part has no script model to remove.");

        return InventorTransactions.Run(_app, part, "GEA: remove script model", () =>
        {
            DeleteOwnedGeometry(part, binding);
            var stale = new List<Inv.UserParameter>();
            foreach (Inv.UserParameter p in part.ComponentDefinition.Parameters.UserParameters)
                if (GeaOwnership.IsOwned(p, binding)) stale.Add(p);
            foreach (var p in stale)
            {
                try { p.Delete(); }
                catch (Exception ex) { Log.Warn($"Could not delete parameter {p.Name}: {ex.Message}"); }
            }
            part.AttributeSets[ScriptSet].Delete();
        }, "Script model removed.");
    }

    // ---------------------------------------------------------------- tiện ích

    private static string Short(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";

    private static string? ReadBinding(Inv.PartDocument part)
    {
        try
        {
            if (!part.AttributeSets.NameIsUsed[ScriptSet]) return null;
            var set = part.AttributeSets[ScriptSet];
            return set.NameIsUsed["Binding"] ? set["Binding"].Value as string : null;
        }
        catch { return null; }
    }

    private static void WriteBinding(Inv.PartDocument part, string binding, GeaScript script, GeaFormResult form, string? sourcePath)
    {
        var set = part.AttributeSets.NameIsUsed[ScriptSet] ? part.AttributeSets[ScriptSet] : part.AttributeSets.Add(ScriptSet);

        // Giá trị form theo đơn vị hiển thị, chỉ các ô đang hiện (ô ẩn lấy lại mặc định khi nạp)
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in form.Fields.Where(f => f.Visible))
            values[f.Name] = f.Kind switch
            {
                GeaFieldKind.Number => Math.Round(f.DisplayNumber(f.Value), 9),
                GeaFieldKind.Checkbox => f.Value.Bool,
                _ => f.Value.Str,
            };

        void Put(string key, string value)
        {
            if (set.NameIsUsed[key]) set[key].Value = value;
            else set.Add(key, Inv.ValueTypeEnum.kStringType, value);
        }
        Put("Binding", binding);
        Put("Version", script.Program.Version.ToString(CultureInfo.InvariantCulture));
        Put("Product", script.Program.ProductName);
        Put("Script", script.Text);
        Put("Path", sourcePath ?? "");
        Put("Values", JsonSerializer.Serialize(values));
        Put("AppliedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Xóa feature (từ sau ra trước), sketch và work plane mang dấu sở hữu. Feature của người dùng phụ thuộc vào chúng
    /// được giữ lại (sẽ báo lỗi khi cập nhật và làm Apply bị hủy, không mất dữ liệu).
    /// </summary>
    private static int DeleteOwnedGeometry(Inv.PartDocument part, string binding)
    {
        var def = part.ComponentDefinition;
        int count = 0;

        var features = new List<Inv.PartFeature>();
        foreach (Inv.PartFeature f in def.Features)
            if (GeaOwnership.IsOwned(f, binding)) features.Add(f);
        for (int i = features.Count - 1; i >= 0; i--)
        {
            try
            {
                features[i].Delete(false, true, true);
                count++;
            }
            catch (Exception ex) { Log.Warn("Could not delete feature: " + ex.Message); }
        }

        var sketches = new List<Inv.PlanarSketch>();
        foreach (Inv.PlanarSketch s in def.Sketches)
            if (GeaOwnership.IsOwned(s, binding)) sketches.Add(s);
        foreach (var s in sketches)
        {
            try { s.Delete(); count++; }
            catch (Exception ex) { Log.Warn("Could not delete sketch: " + ex.Message); }
        }

        var planes = new List<Inv.WorkPlane>();
        foreach (Inv.WorkPlane w in def.WorkPlanes)
            if (!w.IsCoordinateSystemElement && GeaOwnership.IsOwned(w, binding)) planes.Add(w);
        foreach (var w in planes)
        {
            try { w.Delete(true); count++; }
            catch (Exception ex) { Log.Warn("Could not delete work plane: " + ex.Message); }
        }
        return count;
    }
}
