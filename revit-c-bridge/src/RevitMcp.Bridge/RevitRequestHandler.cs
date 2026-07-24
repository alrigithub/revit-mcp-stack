using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed class RevitRequestHandler(BridgeRuntime runtime) : IExternalEventHandler
{
    private readonly TransactionCoordinator _transactions = new();
    private static readonly HashSet<string> UiTools = new(StringComparer.Ordinal) { "select_elements", "zoom_to_elements", "open_view" };
    private static readonly HashSet<string> ContextFreeTools = new(StringComparer.Ordinal) { "list_documents", "get_active_context", "reload_python_provider", "reload_tool_provider" };
    public string GetName() => "Revit MCP bounded request dispatcher";

    public void Execute(UIApplication app)
    {
        runtime.HandlerStarted();
        var turn = Stopwatch.StartNew();
        var processed = 0;
        try
        {
            while (processed < 8 && turn.ElapsedMilliseconds < 50 && runtime.Queue.TryDequeue(out var record))
            {
                if (record is null || record.State.IsTerminal()) continue;
                Process(app, record); processed++;
            }
        }
        finally { runtime.HandlerExited(); }
    }

    private void Process(UIApplication app, RequestRecord record)
    {
        var started = Stopwatch.StartNew();
        try
        {
            if (record.Admission.DeadlineUtc <= DateTimeOffset.UtcNow)
            {
                record.Transition(RequestState.ExpiredBeforeStart, errorCode: "expired_before_start", redactedError: "Deadline passed before Revit work began."); return;
            }
            if (record.Admission.Tool == "run_python" && record.Admission.ProviderGeneration != runtime.Providers.CurrentGeneration)
            {
                record.Transition(RequestState.ProviderReloadedBeforeStart, errorCode: "provider_reloaded_before_start", redactedError: "Pinned Python generation was replaced."); return;
            }

            record.Transition(RequestState.Running);
            object result;
            if (ContextFreeTools.Contains(record.Admission.Tool)) result = DispatchContextFree(app, record);
            else
            {
                var (document, uiDocument) = runtime.Documents.Resolve(app, record.Admission.DocumentSession, record.Admission.DocumentGeneration, UiTools.Contains(record.Admission.Tool));
                result = Dispatch(app, document, uiDocument, record);
            }
            var raw = JsonSerializer.SerializeToElement(result);
            var bounded = RequestValidation.BoundJson(raw, 1000, 4096, out var omitted);
            if (omitted.Length > 0)
            {
                bounded = JsonSerializer.SerializeToElement(new { value = bounded, omitted_fields = omitted, deferred_fields = VerificationService.DeferredFields });
            }
            if (System.Text.Encoding.UTF8.GetByteCount(bounded.GetRawText()) > RevitMcp.Contracts.ProtocolConstants.MaxResultBytes)
                throw new RequestDispatchException("result_too_large", "Bounded result exceeded the byte limit; narrow the query.");
            record.Transition(RequestState.Succeeded, bounded);
        }
        catch (ProviderGenerationException ex) { record.Transition(RequestState.Failed, errorCode: "provider_generation_changed_after_start", redactedError: ex.Message); }
        catch (RequestDispatchException ex) { record.Transition(RequestState.Failed, errorCode: ex.Code, redactedError: ex.Message + (ex.Remediation is null ? "" : " Remediation: " + ex.Remediation)); }
        catch (Exception ex) { record.Transition(RequestState.Failed, errorCode: "execution_failed", redactedError: Redaction.Error(ex)); }
        finally
        {
            runtime.Roslyn.DiscardPrepared(record.Admission.RequestId);
            runtime.Log.Add(new(DateTimeOffset.UtcNow, record.Admission.RequestId, record.Admission.DocumentSession, "terminal", record.Admission.Tool,
                record.State.ToString().ToLowerInvariant(), null, started.ElapsedMilliseconds, record.Admission.ProviderGeneration, record.RedactedError, record.Admission.TransactionMode));
        }
    }

    private object DispatchContextFree(UIApplication app, RequestRecord request) => request.Admission.Tool switch
    {
        "list_documents" => new { documents = runtime.Documents.List(app), omitted_fields = Array.Empty<string>() },
        "get_active_context" => app.ActiveUIDocument is null ? new { active = false } : new { active = true, document = runtime.Documents.Describe(app.ActiveUIDocument.Document, true), view_id = app.ActiveUIDocument.ActiveView.Id.Value, view_name = app.ActiveUIDocument.ActiveView.Name },
        "reload_python_provider" => runtime.Providers.Reload(),
        "reload_tool_provider" => runtime.Roslyn.Reload(),
        _ => throw new RequestDispatchException("unknown_tool", request.Admission.Tool)
    };

    private object Dispatch(UIApplication app, Document document, UIDocument? uiDocument, RequestRecord request)
    {
        var args = request.Admission.Arguments;
        return request.Admission.Tool switch
        {
            "run_python" => ExecutePython(app, document, uiDocument, request),
            "run_csharp" => ExecuteDynamic(document, request, () => JsonValue(runtime.Roslyn.Invoke(request.Admission.RequestId, app, document, uiDocument, RequestPayload(args)))),
            "execute_batch" => ExecuteBatch(app, document, uiDocument, request),
            "execute_and_verify" => ExecuteAndVerify(app, document, uiDocument, request),
            "query_elements" => QueryElements(document, args),
            "get_elements" => GetElements(document, args),
            "get_parameters" => GetParameters(document, args),
            "get_warnings" => new { warnings = VerificationService.Warnings(document), omitted_fields = Array.Empty<string>(), deferred_fields = VerificationService.DeferredFields },
            "select_elements" => Select(uiDocument!, args),
            "zoom_to_elements" => Zoom(uiDocument!, args),
            "open_view" => OpenView(document, uiDocument!, args),
            "export_view" => ExportView(document, args),
            _ => throw new RequestDispatchException("unknown_tool", $"Unsupported bridge tool {request.Admission.Tool}.")
        };
    }

    private object ExecuteDynamic(Document document, RequestRecord request, Func<object> action) =>
        _transactions.Execute(document, request.Admission.TransactionMode!, $"Revit MCP {request.Admission.Tool}", action);

    private object ExecutePython(UIApplication app, Document document, UIDocument? uidoc, RequestRecord request)
    {
        var source = request.Admission.Arguments.GetProperty("source").GetString() ?? string.Empty;
        runtime.Providers.Prepare(request.Admission.ProviderGeneration!, source); // IronPython compile before any bridge transaction
        return ExecuteDynamic(document, request, () => JsonValue(runtime.Providers.Execute(request.Admission.ProviderGeneration!, app, document, uidoc, request.Admission.Arguments.GetRawText())));
    }

    private object ExecuteBatch(UIApplication app, Document document, UIDocument? uidoc, RequestRecord request)
    {
        var args = request.Admission.Arguments;
        if (!args.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array) throw new RequestDispatchException("steps_required", "execute_batch requires arguments.steps.");
        var steps = stepsElement.EnumerateArray().ToArray();
        var atomic = !args.TryGetProperty("atomic", out var atomicElement) || atomicElement.GetBoolean();
        foreach (var step in steps.Where(s => s.GetProperty("tool").GetString() == "run_python"))
        {
            var source = step.GetProperty("arguments").GetProperty("source").GetString() ?? string.Empty;
            runtime.Providers.Prepare(request.Admission.ProviderGeneration!, source);
        }
        Func<object> MakeStep(JsonElement step, int index) => () =>
        {
            var tool = step.GetProperty("tool").GetString() ?? throw new RequestDispatchException("step_tool_required", "A batch step has no tool.");
            var stepArgs = step.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
            return DispatchRaw(app, document, uidoc, request.Admission.RequestId + ":" + index, tool, stepArgs, request.Admission.ProviderGeneration);
        };
        if (atomic) return _transactions.ExecuteAtomicBatch(document, $"Revit MCP batch {request.Admission.RequestId}", steps.Select(MakeStep).ToArray());
        var results = new List<object>();
        for (var index = 0; index < steps.Length; index++)
        {
            var mode = steps[index].TryGetProperty("transaction_mode", out var modeElement) ? modeElement.GetString() : null;
            if (mode is null) throw new RequestDispatchException("transaction_mode_required", "Every non-atomic batch step requires transaction_mode.");
            var action = MakeStep(steps[index], index);
            try { results.Add(new { index, state = "succeeded", result = _transactions.Execute(document, mode, $"Revit MCP batch step {index + 1}", action) }); }
            catch (Exception ex) { results.Add(new { index, state = "failed", error = Redaction.Error(ex) }); }
        }
        return new { atomic = false, steps = results };
    }

    private object ExecuteAndVerify(UIApplication app, Document document, UIDocument? uidoc, RequestRecord request)
    {
        var args = request.Admission.Arguments;
        var action = args.GetProperty("action");
        var tool = action.GetProperty("tool").GetString()!;
        var actionArgs = action.GetProperty("arguments");
        var beforeWarnings = document.GetWarnings().Select(w => w.GetFailureDefinitionId().Guid).ToHashSet();
        return _transactions.Execute(document, request.Admission.TransactionMode!, $"Revit MCP verify {request.Admission.Tool}", () =>
        {
            var result = DispatchRaw(app, document, uidoc, request.Admission.RequestId, tool, actionArgs, request.Admission.ProviderGeneration);
            var verifyIds = args.TryGetProperty("element_ids", out var ids) ? ids.EnumerateArray().Select(x => new ElementId(x.GetInt64())).ToArray() : [];
            var elements = verifyIds.Select(document.GetElement).Where(e => e is not null).Select(e => VerificationService.Element(document, e!)).ToArray();
            var warningDelta = document.GetWarnings().Where(w => !beforeWarnings.Contains(w.GetFailureDefinitionId().Guid)).Select(w => w.GetDescriptionText()).Take(100).ToArray();
            return new { execution = result, verification = new { elements, warning_delta = warningDelta, requested_preflights = args.TryGetProperty("preflights", out var p) ? p : (JsonElement?)null }, omitted_fields = Array.Empty<string>(), deferred_fields = VerificationService.DeferredFields };
        });
    }

    // The dynamic-code contract gives EntryPoint.Run the caller's request object, not the full arguments envelope.
    private static string RequestPayload(JsonElement args) =>
        args.TryGetProperty("request", out var request) ? request.GetRawText() : "{}";

    private object DispatchRaw(UIApplication app, Document document, UIDocument? uidoc, string key, string tool, JsonElement args, string? generation) => tool switch
    {
        "run_python" => JsonValue(runtime.Providers.Execute(generation!, app, document, uidoc, args.GetRawText())),
        "run_csharp" => JsonValue(runtime.Roslyn.Invoke(key, app, document, uidoc, RequestPayload(args))),
        "query_elements" => QueryElements(document, args),
        "get_elements" => GetElements(document, args),
        "get_parameters" => GetParameters(document, args),
        _ => throw new RequestDispatchException("batch_tool_unsupported", $"Tool {tool} is not supported inside a batch.")
    };

    private static object QueryElements(Document document, JsonElement args)
    {
        var limit = Math.Clamp(args.TryGetProperty("limit", out var l) ? l.GetInt32() : 100, 1, 1000);
        FilteredElementCollector collector = new(document);
        if (args.TryGetProperty("category_id", out var category)) collector = collector.WherePasses(new ElementCategoryFilter(new ElementId(category.GetInt64())));
        var elements = collector.WhereElementIsNotElementType().Take(limit).Select(e => VerificationService.Element(document, e, 20)).ToArray();
        return new { elements, limit, omitted_fields = Array.Empty<string>(), deferred_fields = VerificationService.DeferredFields };
    }
    private static object GetElements(Document document, JsonElement args)
    {
        var ids = Ids(args); var elements = ids.Select(document.GetElement).Where(e => e is not null).Take(1000).Select(e => VerificationService.Element(document, e!)).ToArray();
        return new { elements, missing_ids = ids.Where(id => document.GetElement(id) is null).Select(id => id.Value).ToArray(), omitted_fields = Array.Empty<string>(), deferred_fields = VerificationService.DeferredFields };
    }
    private static object GetParameters(Document document, JsonElement args) => new { elements = Ids(args).Take(1000).Select(id => document.GetElement(id)).Where(e => e is not null).Select(e => new { element_id = e!.Id.Value, parameters = VerificationService.Parameters(e) }).ToArray(), omitted_fields = Array.Empty<string>() };
    private static object Select(UIDocument uidoc, JsonElement args) { var ids = Ids(args); uidoc.Selection.SetElementIds(ids); return new { selected_ids = ids.Select(x => x.Value).ToArray() }; }
    private static object Zoom(UIDocument uidoc, JsonElement args) { var ids = Ids(args); uidoc.ShowElements(ids); return new { zoomed_ids = ids.Select(x => x.Value).ToArray() }; }
    private static object OpenView(Document doc, UIDocument uidoc, JsonElement args) { var id = new ElementId(args.GetProperty("view_id").GetInt64()); var view = doc.GetElement(id) as View ?? throw new RequestDispatchException("view_not_found", "The requested view does not exist."); uidoc.RequestViewChange(view); return new { requested_view_id = id.Value, view_name = view.Name }; }
    private static object ExportView(Document doc, JsonElement args)
    {
        var directory = Path.GetFullPath(args.GetProperty("output_directory").GetString()!); Directory.CreateDirectory(directory);
        var viewId = new ElementId(args.GetProperty("view_id").GetInt64()); var name = Path.GetFileNameWithoutExtension(args.GetProperty("file_name").GetString() ?? "revit-view");
        var options = new PDFExportOptions { FileName = name, Combine = true };
        var ok = doc.Export(directory, new List<ElementId> { viewId }, options);
        return new { exported = ok, artifact = Path.Combine(directory, name + ".pdf"), note = "File export occurs after any model transaction; file effects are not Revit-undoable." };
    }
    private static ElementId[] Ids(JsonElement args) => args.GetProperty("element_ids").EnumerateArray().Take(1000).Select(x => new ElementId(x.GetInt64())).ToArray();
    private static object JsonValue(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
