using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Cadflow.Api.Mcp;

namespace Cadflow.Api.Chat;

public sealed record ChatTurn(string? Role, string? Content);
public sealed record ChatRequest(List<ChatTurn>? Messages, string? RequestedBy);
public sealed record ConfirmRequest(string? RequestedBy);

/// <summary>
/// Chat tạo job bằng ngôn ngữ tự nhiên. Server giữ API key, chạy vòng lặp tool và stream SSE về web.
/// LLM KHÔNG có công cụ tạo job: nó chỉ đề xuất (propose_job); job chỉ được tạo khi user bấm xác nhận
/// (POST /api/chat/confirm/{draftId}).
/// </summary>
public static class ChatEndpoint
{
    private const int MaxToolRounds = 8;
    private const int MaxTurns = 40;
    private const int MaxChars = 20_000;

    private const string BaseWorkflow = """
        You are the order assistant of CAD Automation. Users describe a 3D part in natural language and you turn it into a job.
        Products and their parameters come from tools: never invent parameter names, ranges or products.

        Workflow
        1. Identify the product (list_products if unsure). Load its parameters with get_product_schema.
        2. Map the description to parameter values. All values are in millimetres, degrees or integer counts: convert units (e.g. "2 m" -> 2000).
        3. Parameters the user did not mention keep their defaults; say so briefly. Ask a short question only when the product or a key dimension is truly ambiguous.
        4. Call validate_job. If there are issues, fix them if the fix is obvious and tell the user what you changed; otherwise ask.
        {STEP5}
        - Use list_configs only if the user asks for an operating or CAD configuration; pass its id as configId.
        - For a pasted .gea script use import_gea, then {FINISH} with scriptId (values keyed by the script's field names).
        - For status questions use get_job / list_jobs. Before cancel_job, ask the user to confirm in plain text.
        Reply in the user's language. Be concise; show a short table of the main values you set.
        """;

    private const string ConfirmStep = "5. When valid, call propose_job. The user then sees a card with a Create button. You cannot create jobs yourself. Tell the user to review the card and press Create.";
    private const string AutoStep = "5. When valid, call create_job with the validated values. It enqueues the job right away; then tell the user the job id and that it is queued. If create_job returns an error, explain it and fix or ask.";

    private static string Prompt(bool auto) =>
        BaseWorkflow.Replace("{STEP5}", auto ? AutoStep : ConfirmStep).Replace("{FINISH}", auto ? "create_job" : "propose_job");

    public static void MapChat(this WebApplication app, string? apiKey, string model, string? baseUrl = null, bool autoCreate = true)
    {
        var systemPrompt = Prompt(autoCreate);
        var toolList = autoCreate ? [.. Tools, CreateJobTool] : Tools;
        AnthropicClient? client = string.IsNullOrWhiteSpace(apiKey) ? null
            : string.IsNullOrWhiteSpace(baseUrl) ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };

        app.MapPost("/api/chat/confirm/{draftId}", (string draftId, ConfirmRequest req, CadTools tools) =>
        {
            var r = tools.Confirm(draftId, req.RequestedBy);
            return r.Ok ? Results.Created($"/api/jobs/{r.Job!.Id}", r.Job) : Results.Json(r.Error, statusCode: r.Status);
        }).RequireRateLimiting("chat");

        app.MapPost("/api/chat", async (ChatRequest req, CadTools tools, HttpContext http) =>
        {
            if (client == null)
                return Results.Json(new { error = "Chat is not configured. Set Anthropic:ApiKey (dotnet user-secrets) or ANTHROPIC_API_KEY." }, statusCode: 503);

            var turns = (req.Messages ?? new()).Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Role is "user" or "assistant").ToList();
            if (turns.Count == 0 || turns[^1].Role != "user") return Results.BadRequest(new { error = "The last message must come from the user." });
            if (turns.Count > MaxTurns || turns.Sum(t => t.Content!.Length) > MaxChars)
                return Results.BadRequest(new { error = "The conversation is too long. Start a new chat." });

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var ct = http.RequestAborted;
            async Task Send(object evt)
            {
                await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            try
            {
                var messages = new List<MessageParam>();
                foreach (var t in turns)
                    messages.Add(t.Role == "user" ? new UserMessageParam { Content = t.Content! } : new AssistantMessageParam { Content = t.Content! });

                for (var round = 0; round < MaxToolRounds; round++)
                {
                    var response = await client.Messages.Create(new MessageCreateParams
                    {
                        Model = model,
                        MaxTokens = 8000,
                        System = systemPrompt,
                        Tools = toolList,
                        Messages = messages,
                    }, ct);

                    var assistant = new List<ContentBlockParam>();
                    var results = new List<ContentBlockParam>();
                    foreach (var block in response.Content)
                    {
                        if (block.TryPickText(out TextBlock? text))
                        {
                            assistant.Add(new TextBlockParam { Text = text.Text });
                            if (!string.IsNullOrWhiteSpace(text.Text)) await Send(new { type = "text", text = text.Text });
                        }
                        else if (block.TryPickThinking(out ThinkingBlock? thinking))
                            assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                        else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                            assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                        else if (block.TryPickToolUse(out ToolUseBlock? use))
                        {
                            assistant.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                            await Send(new { type = "tool", name = use.Name });
                            var (output, isError, draft, job) = Run(tools, use.Name, use.Input, req.RequestedBy, autoCreate);
                            if (draft != null)
                                await Send(new { type = "proposal", draftId = draft.Id, product = draft.Product, values = draft.Values, scriptId = draft.Request.ScriptId, configId = draft.Request.ConfigId });
                            if (job != null)
                                await Send(new { type = "job", jobId = job.Id, product = job.ProductCode, values = job.Inputs });
                            results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = output, IsError = isError });
                        }
                    }

                    if (response.StopReason == "refusal") { await Send(new { type = "error", error = "The request was declined." }); break; }
                    if (response.StopReason != "tool_use" || results.Count == 0) break;
                    messages.Add(new AssistantMessageParam { Content = assistant });
                    messages.Add(new UserMessageParam { Content = results });
                    if (round == MaxToolRounds - 1) await Send(new { type = "error", error = "Too many tool steps. Please rephrase or be more specific." });
                }
                await Send(new { type = "done" });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Chat failed");
                await Send(new { type = "error", error = "The assistant failed. Please try again." });
            }
            return Results.Empty;
        }).RequireRateLimiting("chat");
    }

    // ---------------------------------------------------------------- tool dispatch

    private static (string Output, bool IsError, Draft? Draft, Job? Job) Run(CadTools t, string name, IReadOnlyDictionary<string, JsonElement> input, string? requestedBy, bool autoCreate)
    {
        try
        {
            string? S(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            Dictionary<string, double>? Nums(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.GetDouble()) : null;
            Dictionary<string, string>? Strs(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!) : null;

            switch (name)
            {
                case "list_products": return (t.ListProducts(), false, null, null);
                case "get_product_schema": return (t.GetProductSchema(S("code") ?? "", S("configId")), false, null, null);
                case "list_configs": return (t.ListConfigs(S("code") ?? ""), false, null, null);
                case "validate_job": return (t.ValidateJob(S("code") ?? "", Nums("values"), S("configId")), false, null, null);
                case "propose_job":
                    var (json, draft) = t.ProposeJob(S("code"), Nums("values"), S("configId"), S("scriptId"), Strs("textInputs"), requestedBy);
                    return (json, false, draft, null);
                case "create_job" when autoCreate:
                    var r = t.CreateNow(S("code"), Nums("values"), S("configId"), S("scriptId"), Strs("textInputs"), requestedBy);
                    return r.Ok ? (JsonSerializer.Serialize(CadTools.JobView(r.Job!)), false, null, r.Job)
                                : (JsonSerializer.Serialize(r.Error), true, null, null);
                case "import_gea": return (t.ImportGea(S("text") ?? ""), false, null, null);
                case "get_job": return (t.GetJob(S("jobId") ?? ""), false, null, null);
                case "list_jobs": return (t.ListJobs(S("status"), S("code"), input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 10), false, null, null);
                case "cancel_job": return (t.CancelJob(S("jobId") ?? ""), false, null, null);
                default: return ($"{{\"error\":\"Unknown tool '{name}'.\"}}", true, null, null);
            }
        }
        catch (Exception ex) { return (JsonSerializer.Serialize(new { error = ex.Message }), true, null, null); }
    }

    // ---------------------------------------------------------------- tool schemas (create_job chỉ có khi Chat:AutoCreate bật)

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);
    private static readonly object Str = new { type = "string" };
    private static readonly object NumMap = new { type = "object", additionalProperties = new { type = "number" }, description = "parameter name -> number (mm, degrees or integer count)" };

    private static ToolUnion T(string name, string desc, Dictionary<string, JsonElement>? props = null, string[]? required = null) =>
        new Tool { Name = name, Description = desc, InputSchema = new() { Properties = props ?? new(), Required = required ?? [] } };

    private static readonly ToolUnion CreateJobTool = T("create_job",
        "Create (enqueue) the job right away. Call only after validate_job passes. Use code for a built-in product or scriptId for an imported .gea script.",
        new() { ["code"] = P(Str), ["values"] = P(NumMap), ["configId"] = P(Str), ["scriptId"] = P(Str),
                ["textInputs"] = P(new { type = "object", additionalProperties = new { type = "string" } }) });

    private static readonly List<ToolUnion> Tools =
    [
        T("list_products", "List orderable products (code, name, description)."),
        T("get_product_schema", "Get a product's parameters with unit, integer flag, default, min and max. Values are in mm, degrees or counts.",
            new() { ["code"] = P(Str), ["configId"] = P(Str) }, ["code"]),
        T("list_configs", "List ACTIVE configurations (operation / CAD conditions) of a product.", new() { ["code"] = P(Str) }, ["code"]),
        T("validate_job", "Validate values for a product without creating anything; returns complete values and rule violations.",
            new() { ["code"] = P(Str), ["values"] = P(NumMap), ["configId"] = P(Str) }, ["code"]),
        T("propose_job", "Propose a job for the user to confirm. Does NOT create it: the user must press Create. Call only after validate_job passes. Use code for a built-in product or scriptId for an imported .gea script.",
            new() { ["code"] = P(Str), ["values"] = P(NumMap), ["configId"] = P(Str), ["scriptId"] = P(Str),
                    ["textInputs"] = P(new { type = "object", additionalProperties = new { type = "string" } }) }),
        T("import_gea", "Import .gea script text; returns scriptId and its form.", new() { ["text"] = P(Str) }, ["text"]),
        T("get_job", "Get a job's status, step, inputs, summary and error.", new() { ["jobId"] = P(Str) }, ["jobId"]),
        T("list_jobs", "List recent jobs (newest first).",
            new() { ["status"] = P(new { type = "string", @enum = new[] { "queued", "running", "succeeded", "failed", "cancelled" } }), ["code"] = P(Str), ["limit"] = P(new { type = "integer" }) }),
        T("cancel_job", "Cancel a queued or running job. Ask the user to confirm first.", new() { ["jobId"] = P(Str) }, ["jobId"]),
    ];
}
