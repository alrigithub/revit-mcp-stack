using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcp.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed class RevitRequestHandler(BridgeRuntime runtime) : IExternalEventHandler
{
    private readonly TransactionCoordinator _transactions = new();
    private readonly List<string> _revitFailures = new();
    private bool _bypassDialogs = true;
    private int _added, _modified, _deleted;
    private static readonly HashSet<string> UiTools = new(StringComparer.Ordinal) { "select_elements", "zoom_to_elements", "open_view" };
    private static readonly HashSet<string> ContextFreeTools = new(StringComparer.Ordinal) { "list_documents", "get_active_context", "reload_python_provider", "reload_tool_provider" };
    public string GetName() => "Revit MCP bounded request dispatcher";

    public void Execute(UIApplication app)
    {
        runtime.HandlerStarted();
        var turn = Stopwatch.StartNew();
        var processed = 0;
        // Bridge work runs headless: a modal dialog would block the queue until a
        // human dismisses it in Revit. While the handler owns the UI thread, failures
        // are resolved without UI and dialogs are answered automatically; both are
        // reported through the request's error text and the operational log.
        // Operators can turn the bypass off in Settings to get stock Revit dialogs.
        _bypassDialogs = LocalSettingsStore.Load().BypassDialogs;
        if (_bypassDialogs)
        {
            app.DialogBoxShowing += OnDialogBoxShowing;
            app.Application.FailuresProcessing += OnFailuresProcessing;
        }
        app.Application.DocumentChanged += OnDocumentChanged;
        try
        {
            while (processed < 8 && turn.ElapsedMilliseconds < 50 && runtime.Queue.TryDequeue(out var record))
            {
                if (record is null || record.State.IsTerminal()) continue;
                Process(app, record); processed++;
            }
        }
        finally
        {
            if (_bypassDialogs)
            {
                app.DialogBoxShowing -= OnDialogBoxShowing;
                app.Application.FailuresProcessing -= OnFailuresProcessing;
            }
            app.Application.DocumentChanged -= OnDocumentChanged;
            runtime.HandlerExited();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        _added += args.GetAddedElementIds().Count;
        _modified += args.GetModifiedElementIds().Count;
        _deleted += args.GetDeletedElementIds().Count;
    }

    private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs args)
    {
        var text = args switch
        {
            TaskDialogShowingEventArgs task => task.Message,
            MessageBoxShowingEventArgs box => box.Message,
            _ => null
        };
        var answer = args is TaskDialogShowingEventArgs ? "Cancel" : "OK";
        _revitFailures.Add($"dialog[{args.DialogId}]{(string.IsNullOrWhiteSpace(text) ? "" : $" \"{text}\"")} auto-answered {answer}");
        args.OverrideResult(args is TaskDialogShowingEventArgs ? (int)TaskDialogResult.Cancel : 1 /* IDOK */);
    }

    private void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs args)
    {
        var accessor = args.GetFailuresAccessor();
        var failures = accessor.GetFailureMessages();
        if (failures.Count == 0) return;
        var blocking = false;
        foreach (var failure in failures)
        {
            var severity = failure.GetSeverity();
            blocking |= severity != FailureSeverity.Warning;
            _revitFailures.Add($"{severity}: {failure.GetDescriptionText()}");
        }
        if (blocking) { args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack); return; }
        accessor.DeleteAllWarnings();
        args.SetProcessingResult(FailureProcessingResult.Continue);
    }

    private string WithRevitFailures(string message)
    {
        if (_revitFailures.Count == 0) return message;
        var summary = string.Join("; ", _revitFailures.Take(5));
        return $"{message} Revit reported: {summary[..Math.Min(summary.Length, 700)]}";
    }

    private void Process(UIApplication app, RequestRecord record)
    {
        var started = Stopwatch.StartNew();
        _revitFailures.Clear();
        _added = _modified = _deleted = 0;
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
        catch (RequestDispatchException ex) { record.Transition(RequestState.Failed, errorCode: ex.Code, redactedError: WithRevitFailures(ex.Message + (ex.Remediation is null ? "" : " Remediation: " + ex.Remediation))); }
        catch (Exception ex) { record.Transition(RequestState.Failed, errorCode: "execution_failed", redactedError: WithRevitFailures(Redaction.Error(ex))); }
        finally
        {
            runtime.Roslyn.DiscardPrepared(record.Admission.RequestId);
            if (record.State == RequestState.Succeeded && _revitFailures.Count > 0)
                runtime.Log.Add(new(DateTimeOffset.UtcNow, record.Admission.RequestId, record.Admission.DocumentSession, "revit_notices", record.Admission.Tool,
                    "suppressed", null, null, Truncate(string.Join("; ", _revitFailures), 300), null, record.Admission.TransactionMode));
            runtime.Log.Add(new(DateTimeOffset.UtcNow, record.Admission.RequestId, record.Admission.DocumentSession, "terminal", record.Admission.Tool,
                record.State.ToString().ToLowerInvariant(), null, started.ElapsedMilliseconds, record.Admission.ProviderGeneration, record.RedactedError, record.Admission.TransactionMode,
                BuildSummary(), Label(record)));
        }
    }

    private static string? Label(RequestRecord record) =>
        record.Admission.Arguments.ValueKind == JsonValueKind.Object
        && record.Admission.Arguments.TryGetProperty("label", out var label)
        && label.ValueKind == JsonValueKind.String
        && label.GetString() is { Length: > 0 } text
            ? Truncate(text, 120)
            : null;

    private string? BuildSummary()
    {
        var parts = new List<string>();
        if (_added + _modified + _deleted > 0) parts.Add($"+{_added} ~{_modified} -{_deleted}");
        if (_revitFailures.Count > 0) parts.Add(_revitFailures.Count == 1 ? "1 Revit notice" : $"{_revitFailures.Count} Revit notices");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private object DispatchContextFree(UIApplication app, RequestRecord request) => request.Admission.Tool switch
    {
        "list_documents" => new { documents = runtime.Documents.List(app), omitted_fields = Array.Empty<string>() },
        "get_active_context" => ActiveContext(app),
        "reload_python_provider" => runtime.Providers.Reload(),
        "reload_tool_provider" => runtime.Roslyn.Reload(),
        _ => throw new RequestDispatchException("unknown_tool", request.Admission.Tool)
    };

    // A closed document (EditFamily leftover, overnight session) can linger behind
    // ActiveUIDocument; touching its members throws InvalidObjectException, which
    // for this tool simply means there is no usable active context.
    private object ActiveContext(UIApplication app)
    {
        try
        {
            var uidoc = app.ActiveUIDocument;
            var document = uidoc?.Document;
            if (uidoc is null || document is null || !document.IsValidObject) return new { active = false };
            return new { active = true, document = runtime.Documents.Describe(document, true), view_id = uidoc.ActiveView.Id.Value, view_name = uidoc.ActiveView.Name };
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return new { active = false }; }
    }

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
