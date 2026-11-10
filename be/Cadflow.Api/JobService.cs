using InventorAddin.Gea;
using InventorAddin.Models;

namespace Cadflow.Api;

/// <summary>Kết quả tạo job: hoặc một Job (201), hoặc mã lỗi HTTP kèm nội dung lỗi.</summary>
public sealed record CreateJobResult(Job? Job, int Status, object? Error)
{
    public bool Ok => Job != null;
}

/// <summary>
/// Logic đặt job dùng chung cho REST (POST /api/jobs), công cụ MCP và chat: server luôn kiểm tra lại lần cuối,
/// không tin dữ liệu từ trình duyệt hay từ LLM.
/// </summary>
public sealed class JobService(JobStore store, ScriptStore scripts, ConfigStore configs, ILogger<JobService> log)
{
    public const string ImportedProduct = "imported"; // mã sản phẩm của job đặt từ script .gea do người dùng import

    public CreateJobResult Create(CreateJobRequest req)
    {
        var requestedByName = string.IsNullOrWhiteSpace(req.RequestedBy) ? "anonymous" : req.RequestedBy.Trim();
        if (requestedByName.Length > 80) requestedByName = requestedByName[..80];

        // Đặt hàng từ script .gea đã import: server biên dịch lại và tính FORM lần cuối.
        if (!string.IsNullOrWhiteSpace(req.ScriptId))
        {
            if (scripts.Get(req.ScriptId) is not { } scriptSource)
                return new(null, 404, new { error = "Unknown script. Import the .gea file again." });
            var script = GeaScript.Compile(scriptSource, GeaLibraries.Resolve);
            if (GeaForms.Reject(script) is { } why)
                return new(null, 400, new { error = why, diagnostics = GeaForms.Diagnostics(script, errorsOnly: true) });
            var form = GeaForms.Evaluate(script, req.Inputs, req.TextInputs);
            if (!form.Ok)
                return new(null, 400, new { error = "The order violates the script's rules.", issues = form.Errors.Select(e => new { message = e.Message, @params = e.Params }) });

            var name = string.IsNullOrWhiteSpace(script.Program.ProductName) ? "imported.gea" : script.Program.ProductName;
            var (numbers, texts) = GeaForms.Complete(form);
            return new(store.Create(ImportedProduct, numbers, requestedByName, name, req.ScriptId, "imported", texts), 201, null);
        }

        var product = Products.Find(string.IsNullOrWhiteSpace(req.ProductCode) ? "box" : req.ProductCode);
        if (product == null) return new(null, 404, new { error = $"Unknown product '{req.ProductCode}'." });

        ProductConfig? chosen = null;
        if (!string.IsNullOrWhiteSpace(req.ConfigId))
        {
            chosen = configs.Get(product, req.ConfigId);
            if (chosen == null) return new(null, 404, new { error = "Unknown configuration." });
            if (chosen.Status != "active") return new(null, 400, new { error = $"Configuration '{chosen.Name}' is {chosen.Status}; only active configurations can be ordered." });
        }
        var values = configs.Effective(product, req.Inputs, chosen);
        var issues = product.Validate(values);
        if (issues.Count > 0)
            return new(null, 400, new { error = "The order violates product rules.", issues });

        string? scriptSha = null;
        if (product.ScriptFile != null)
        {
            if (ScriptStore.ReadBundled(product.ScriptFile) is { } scriptText)
                scriptSha = scripts.Save(GeaScript.Compile(scriptText, GeaLibraries.Resolve).ExpandedText); // worker nhận bản đã mở rộng INCLUDE
            else log.LogWarning("Bundled script {File} for product {Product} was not found; the job will have no .gea export.", product.ScriptFile, product.Code);
        }

        var job = store.Create(product.Code, values, requestedByName,
            scriptSha == null ? null : product.ScriptFile, scriptSha, scriptSha == null ? null : "bundled");
        return new(job, 201, null);
    }

    public sealed record Reply(int Status, object Body);

    /// <summary>Import nội dung .gea (có thể kèm khối giá trị !@), biên dịch, lưu theo mã băm và trả form.</summary>
    public Reply Import(string? input)
    {
        var text = input ?? "";
        if (text.Trim().Length == 0) return new(400, new { error = "The file is empty." });
        if (System.Text.Encoding.UTF8.GetByteCount(text) > GeaForms.MaxScriptBytes)
            return new(400, new { error = $"The file is larger than {GeaForms.MaxScriptBytes / 1024} KB." });

        var block = GeaExport.ParseBlock(text);
        var clean = GeaExport.StripBlock(text);
        var script = GeaScript.Compile(clean, GeaLibraries.Resolve);
        if (GeaForms.Reject(script) is { } why)
            return new(400, new { error = why, diagnostics = GeaForms.Diagnostics(script) });

        var names = GeaForms.FieldNames(script).ToHashSet(StringComparer.Ordinal);
        var values = block?.Values.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, double>();
        var texts = block?.Texts.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>();
        var ignored = (block?.Values.Keys.Concat(block.Texts.Keys) ?? Enumerable.Empty<string>()).Where(k => !names.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        var warnings = new List<string>();
        if (block?.ScriptSha256 is { } exportedSha && exportedSha != ScriptStore.Sha256(clean))
            warnings.Add("The script was modified after this file was exported; the values may no longer fit it.");
        if (ignored.Length > 0)
            warnings.Add($"{ignored.Length} value(s) in the file do not match any parameter of the script and were ignored: {string.Join(", ", ignored.Take(5))}{(ignored.Length > 5 ? ", ..." : "")}.");

        var id = scripts.Save(script.ExpandedText); // lưu bản đã mở rộng INCLUDE: tự chạy được, worker không cần thư viện
        return new(200, new
        {
            scriptId = id,
            name = script.Program.ProductName,
            hasValues = block != null,
            values,
            texts,
            ignored,
            warnings,
            diagnostics = GeaForms.Diagnostics(script),
            form = GeaForms.Describe(GeaForms.Evaluate(script, values, texts)),
        });
    }

    /// <summary>Form của script đã import, tính theo giá trị hiện tại (form có thể ẩn/hiện ô theo giá trị).</summary>
    public Reply ScriptForm(string id, IReadOnlyDictionary<string, double>? values, IReadOnlyDictionary<string, string>? texts)
    {
        if (scripts.Get(id) is not { } text) return new(404, new { error = "Unknown script. Import the .gea file again." });
        var script = GeaScript.Compile(text, GeaLibraries.Resolve);
        if (GeaForms.Reject(script) is { } why) return new(400, new { error = why });
        return new(200, new { scriptId = id, name = script.Program.ProductName, form = GeaForms.Describe(GeaForms.Evaluate(script, values, texts)) });
    }
}
