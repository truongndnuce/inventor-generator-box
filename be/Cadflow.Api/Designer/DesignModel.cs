using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadflow.Api;

// The design model of the Product Designer (docs/superpowers/specs/2026-11-11-gea-designer-design.md, section 2).
// The model is what the web page edits; DesignGenerator turns it into a .gea script.
// Every property has a default so a sparse JSON body (missing arrays, explicit nulls) deserializes without throwing;
// DesignValidator then reports what is missing. The lists are plain mutable lists, so two models are compared by their JSON (DesignJson.Serialize).

/// <summary>The whole design. <see cref="Version"/> is the version of the model format (1).</summary>
public sealed record DesignModel
{
    public int Version { get; init; } = 1;
    public DesignProduct Product { get; set; } = new();
    public List<DesignGroup> Groups { get; init; } = new();
    public List<DesignRule> Rules { get; init; } = new();
    public List<DesignVar> Vars { get; init; } = new();
    public List<DesignStep> Steps { get; init; } = new();
}

/// <param name="Name">PRODUCT_NAME.</param>
/// <param name="Prefix">PARAM_PREFIX, for example <c>FN_</c>; every parameter name must start with it.</param>
/// <param name="Target">TARGET; only <c>PART</c> in this version.</param>
public sealed record DesignProduct
{
    public string Name { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Target { get; init; } = "PART";
}

/// <summary>A BEGIN_GROUP block of the form.</summary>
public sealed class DesignGroup
{
    public string Title { get; set; } = "";
    public List<DesignParam> Params { get; set; } = new();
}

/// <summary>One input field of the form. <see cref="Type"/> is LENGTH, ANGLE, INTEGER, DOUBLE, STRING, CHECKBOX or RADIO.</summary>
/// <remarks><see cref="Default"/> is text so one field holds a number, TRUE/FALSE or a choice; it is parsed by <see cref="Type"/>.
/// <see cref="VisibleWhen"/> is an expression: the field is shown (and has a value) only while it is true.</remarks>
public sealed class DesignParam
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Default { get; set; }
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public bool Required { get; set; }
    public string? Label { get; set; }
    public string? Tooltip { get; set; }
    public string? VisibleWhen { get; set; }
    public List<string>? Options { get; set; }
}

/// <summary>An ASSERT: <see cref="Expr"/> must hold, otherwise <see cref="Message"/> is shown (as a warning when <see cref="Warn"/>).</summary>
public sealed class DesignRule
{
    public string Expr { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Warn { get; set; }
}

/// <summary>A variable computed from the parameters (DECLARE_VARIABLE + assignment), usable in rules and in the build text.</summary>
public sealed class DesignVar
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Expr { get; set; } = "";
}

/// <summary>A build step. Only <c>raw</c> (verbatim text inside BEGIN_BUILD) exists in this version.</summary>
public sealed class DesignStep
{
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
}

/// <summary>JSON used for the model everywhere (the API and the !@DESIGNER block): web defaults, nulls left out, so a model always serializes the same way.</summary>
public static class DesignJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(DesignModel model) => JsonSerializer.Serialize(model, Options);

    /// <summary>The model, or null when the text is not JSON or not a model (never throws for bad input).</summary>
    public static DesignModel? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<DesignModel>(json, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
