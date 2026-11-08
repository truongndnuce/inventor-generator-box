using System.Globalization;
using InventorAddin.Gea;

namespace Cadflow.Api;

/// <summary>
/// Cầu nối giữa web và trình thông dịch .gea cho script do người dùng import: kiểm tra script có được phép chạy,
/// tính FORM (hàm thuần, không đụng Inventor) và đổi kết quả sang JSON cho giao diện.
/// Giá trị vào/ra theo <b>đơn vị hiển thị của ô</b> (đúng như khai báo trong USER_INPUT_PARAM); ô tích là 1/0.
/// </summary>
public static class GeaForms
{
    public const int MaxScriptBytes = 256 * 1024;
    public const int MaxFields = 200;

    public static object[] Diagnostics(GeaScript script, bool errorsOnly = false) =>
        script.Diagnostics
            .Where(d => !errorsOnly || d.Severity == GeaSeverity.Error)
            .Select(d => (object)new
            {
                code = d.Code,
                severity = d.Severity == GeaSeverity.Error ? "error" : "warning",
                line = d.Line, column = d.Column, message = d.Message,
            })
            .ToArray();

    /// <summary>Lý do script không chạy được trên web, hoặc null nếu được phép.</summary>
    public static string? Reject(GeaScript script)
    {
        if (script.HasErrors) return "The script has errors.";
        if (script.Program.Target != "PART") return $"TARGET {script.Program.Target} is not supported yet (only PART).";
        var fields = script.EvaluateForm(null).Fields;
        if (fields.Count > MaxFields) return $"The form has {fields.Count} fields; at most {MaxFields} are allowed.";
        return null;
    }

    public static IReadOnlyList<string> FieldNames(GeaScript script) => script.EvaluateForm(null).Fields.Select(f => f.Name).ToList();

    /// <summary>Tính FORM với giá trị người dùng nhập (ô tích đổi từ 1/0 sang bool; ô text/radio dùng <paramref name="texts"/>). Tên không có trong FORM bị bỏ qua.</summary>
    public static GeaFormResult Evaluate(GeaScript script, IReadOnlyDictionary<string, double>? values, IReadOnlyDictionary<string, string>? texts = null) =>
        script.EvaluateFormValues(values, texts);

    /// <summary>Giá trị đầy đủ của mọi ô (ô ẩn lấy mặc định), dùng làm đầu vào đã chuẩn hóa của job.</summary>
    public static (Dictionary<string, double> Numbers, Dictionary<string, string> Texts) Complete(GeaFormResult form)
    {
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in form.Fields)
        {
            if (f.Kind is GeaFieldKind.Text or GeaFieldKind.Radio) texts[f.Name] = f.Value.Str ?? "";
            else numbers[f.Name] = f.Kind == GeaFieldKind.Checkbox ? (f.Value.Bool ? 1 : 0) : f.DisplayNumber(f.Value);
        }
        return (numbers, texts);
    }

    public static object Describe(GeaFormResult form) => new
    {
        ok = form.Ok,
        fields = form.Fields.Select(f => new
        {
            name = f.Name,
            label = f.Label,
            tooltip = f.Tooltip,
            group = f.Group,
            kind = f.Kind switch { GeaFieldKind.Checkbox => "checkbox", GeaFieldKind.Radio => "radio", GeaFieldKind.Text => "text", _ => "number" },
            options = f.Options,
            unit = f.Unit,
            integer = f.Type == GeaType.Integer,
            min = f.Min.HasValue ? (double?)Round(f.Min.Value / f.UnitFactor) : null,
            max = f.Max.HasValue ? (double?)Round(f.Max.Value / f.UnitFactor) : null,
            step = f.Step,
            required = f.Required,
            visible = f.Visible,
            value = Scalar(f, f.Value),
            @default = Scalar(f, f.Default),
            errors = f.Errors,
        }),
        errors = form.Errors.Select(e => new { message = e.Message, @params = e.Params, line = e.Line }),
        warnings = form.Warnings.Select(e => new { message = e.Message, @params = e.Params, line = e.Line }),
        notes = form.Notes,
    };

    /// <summary>Giá trị một ô để gửi ra giao diện: chữ cho text/radio, 1/0 cho ô tích, số (đơn vị hiển thị) cho ô số.</summary>
    private static object Scalar(GeaFormField f, GeaValue v) => f.Kind switch
    {
        GeaFieldKind.Text or GeaFieldKind.Radio => v.Str ?? "",
        GeaFieldKind.Checkbox => v.Bool ? 1 : 0,
        _ => Round(f.DisplayNumber(v)),
    };

    private static double Round(double v) => double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
