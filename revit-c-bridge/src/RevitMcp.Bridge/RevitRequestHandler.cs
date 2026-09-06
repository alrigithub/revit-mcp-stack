using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcp.Core;
using RevitMcp.Contracts;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed class RevitRequestHandler(BridgeRuntime runtime) : IExternalEventHandler
{
    private readonly TransactionCoordinator _transactions = new();
    private readonly List<object> _revitFailures = new();
    private int _noticeCount;
    private readonly HashSet<long> _addedIds = new(), _modifiedIds = new(), _deletedIds = new();
    private bool _bypassDialogs = true;
    private int _added, _modified, _deleted;
    private static readonly HashSet<string> UiTools = new(StringComparer.Ordinal) { "select_elements", "zoom_to_elements", "open_view" };
    private static readonly HashSet<string> ContextFreeTools = new(StringComparer.Ordinal) { "list_documents", "get_active_context", "reload_python_provider", "reload_tool_provider" };
    public string GetName() => "3XN-RevitMCP bounded request dispatcher";

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
        app.DialogBoxShowing += OnDialogBoxShowing;
        app.Application.FailuresProcessing += OnFailuresProcessing;
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
            app.DialogBoxShowing -= OnDialogBoxShowing;
            app.Application.FailuresProcessing -= OnFailuresProcessing;
            app.Application.DocumentChanged -= OnDocumentChanged;
            runtime.HandlerExited();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        _added += args.GetAddedElementIds().Count;
        _modified += args.GetModifiedElementIds().Count;
        _deleted += args.GetDeletedElementIds().Count;
        foreach (var id in args.GetAddedElementIds()) if (_addedIds.Count < 1000) _addedIds.Add(id.Value);
        foreach (var id in args.GetModifiedElementIds()) if (_modifiedIds.Count < 1000) _modifiedIds.Add(id.Value);
        foreach (var id in args.GetDeletedElementIds()) if (_deletedIds.Count < 1000) _deletedIds.Add(id.Value);
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
        Notice(new { kind = "dialog", dialog_id = args.DialogId, description = Truncate(text ?? "", 500), response = _bypassDialogs ? answer : "operator", suppressed = _bypassDialogs });
        if (_bypassDialogs) args.OverrideResult(args is TaskDialogShowingEventArgs ? (int)TaskDialogResult.Cancel : 1 /* IDOK */);
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
            Notice(new { kind = "failure", failure_definition_id = failure.GetFailureDefinitionId().Guid.ToString("D"), severity = severity.ToString(),
                description = Truncate(failure.GetDescriptionText(), 500), involved_element_ids = failure.GetFailingElementIds().Concat(failure.GetAdditionalElementIds()).Distinct().Take(100).Select(id => id.Value).ToArray(),
                suppressed = _bypassDialogs && severity == FailureSeverity.Warning });
        }
        if (!_bypassDialogs) return;
        if (blocking) { args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack); return; }
        accessor.DeleteAllWarnings();
        args.SetProcessingResult(FailureProcessingResult.Continue);
    }

    private void Notice(object notice)
    {
        _noticeCount++;
        if (_revitFailures.Count < 200) _revitFailures.Add(notice);
    }

    private void Process(UIApplication app, RequestRecord record)
    {
        var started = Stopwatch.StartNew();
        _revitFailures.Clear();
        _noticeCount = 0;
        _addedIds.Clear(); _modifiedIds.Clear(); _deletedIds.Clear();
        _added = _modified = _deleted = 0;
        var outcome = "not_started";
        _transactions.ObserveOutcome = value => outcome = value;
        JsonElement? diagnostics = null, bounded = null;
        string? errorCode = null, errorMessage = null;
        using var controls = ScriptControl.Bind(record.Progress, () => record.CancellationRequested);
        try
        {
            if (record.CancellationRequested)
            {
                record.Transition(RequestState.CancelledBeforeStart, errorCode: "cancelled_before_start", redactedError: "Cancellation observed before Revit work began."); return;
            }
            if (record.Admission.DeadlineUtc <= DateTimeOffset.UtcNow)
            {
                record.Transition(RequestState.ExpiredBeforeStart, errorCode: "expired_before_start", redactedError: "Deadline passed before Revit work began."); return;
            }
            if (record.Admission.ProviderGeneration is not null && record.Admission.ProviderGeneration != runtime.Providers.CurrentGeneration)
            {
                record.Transition(RequestState.ProviderReloadedBeforeStart, errorCode: "provider_reloaded_before_start", redactedError: "Pinned Python generation was replaced."); return;
            }

            record.Transition(RequestState.Running);
            outcome = "no_changes_observed";
            object result;
            if (ContextFreeTools.Contains(record.Admission.Tool)) result = DispatchContextFree(app, record);
            else
            {
                var (document, uiDocument) = runtime.Documents.Resolve(app, record.Admission.DocumentSession, record.Admission.DocumentGeneration, UiTools.Contains(record.Admission.Tool));
                result = Dispatch(app, document, uiDocument, record);
            }
            bounded = ResultProjection.Prepare(result);
        }
        catch (ProviderGenerationException ex) { errorCode = "provider_generation_changed_after_start"; errorMessage = ex.Message; }
        catch (VerificationFailedException ex) { errorCode = "verification_failed"; errorMessage = ex.Message; diagnostics = ex.Details; }
        catch (OperationCanceledException ex) { errorCode = "cooperative_cancelled"; errorMessage = ex.Message; }
        catch (ScriptFailureException ex) { errorCode = "script_failed"; errorMessage = ex.Message; diagnostics = JsonSerializer.Deserialize<JsonElement>(ex.DiagnosticsJson); }
        catch (RequestDispatchException ex) { errorCode = ex.Code; errorMessage = ex.Message + (ex.Remediation is null ? "" : " Remediation: " + ex.Remediation); }
        catch (Exception ex)
        {
            errorCode = "execution_failed"; errorMessage = Redaction.Error(ex);
            diagnostics = JsonSerializer.SerializeToElement(new { exception_type = ex.GetType().Name,
                frames = new StackTrace(ex, true).GetFrames().Where(f => Path.GetFileName(f.GetFileName()) == "agent.cs")
                    .Take(12).Select(f => new { file = "agent.cs", line = f.GetFileLineNumber(), function = f.GetMethod()?.Name }).ToArray() });
        }
        finally
        {
            if (outcome == "no_changes_observed" && _added + _modified + _deleted > 0) outcome = "script_owned";
            record.SetReceipt(JsonSerializer.SerializeToElement(new { model_outcome = outcome, transaction_mode = record.Admission.TransactionMode,
                changes_observed = new { added = _added, modified = _modified, deleted = _deleted, added_ids = _addedIds.ToArray(), modified_ids = _modifiedIds.ToArray(), deleted_ids = _deletedIds.ToArray(), ids_limit = 1000 },
                notices = _revitFailures.ToArray(), omitted_notice_count = Math.Max(0, _noticeCount - _revitFailures.Count), diagnostics,
                file_effects = "Files and external side effects are not covered by Revit rollback." }));
            if (!record.State.IsTerminal()) record.Transition(errorCode is null ? RequestState.Succeeded : RequestState.Failed, bounded, errorCode, errorMessage);
            runtime.Roslyn.DiscardPrepared(record.Admission.RequestId);
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
        "reload_python_provider" => ReloadPythonProvider(),
        "reload_tool_provider" => runtime.Roslyn.Reload(),
        _ => throw new RequestDispatchException("unknown_tool", request.Admission.Tool)
    };

    private object ReloadPythonProvider()
    {
        var result = runtime.Providers.Reload();
        runtime.RefreshRibbon();
        return result;
    }

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
            return new { active = true, document = runtime.Documents.Describe(document, true), view_id = uidoc.ActiveView.Id.Value, view_name = uidoc.ActiveView.Name, edit_mode = ReflectionProbes.ActiveEditMode(document) };
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
            "capture_model" => CaptureService.Capture(document, args, _transactions.ObserveOutcome),
            _ => throw new RequestDispatchException("unknown_tool", $"Unsupported bridge tool {request.Admission.Tool}.")
        };
    }

    private object ExecuteDynamic(Document document, RequestRecord request, Func<object> action) =>
        _transactions.Execute(document, request.Admission.TransactionMode!, $"3XN-RevitMCP {request.Admission.Tool}", action);

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
        if (atomic) return _transactions.ExecuteAtomicBatch(document, $"3XN-RevitMCP batch {request.Admission.RequestId}", steps.Select(MakeStep).ToArray());
        var results = new List<object>();
        var observer = _transactions.ObserveOutcome;
        var stepOutcome = "not_started";
        _transactions.ObserveOutcome = value => stepOutcome = value;
        try
        {
        for (var index = 0; index < steps.Length; index++)
        {
            var mode = steps[index].TryGetProperty("transaction_mode", out var modeElement) ? modeElement.GetString() : null;
            if (mode is null) throw new RequestDispatchException("transaction_mode_required", "Every non-atomic batch step requires transaction_mode.");
            var action = MakeStep(steps[index], index);
            stepOutcome = "not_started";
            try { var value = _transactions.Execute(document, mode, $"3XN-RevitMCP batch step {index + 1}", action); results.Add(new { index, state = "succeeded", model_outcome = stepOutcome, result = value }); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(new { index, state = "failed", model_outcome = stepOutcome, error = Redaction.Error(ex) }); }
        }
        return new { atomic = false, steps = results };
        }
        finally { _transactions.ObserveOutcome = observer; observer?.Invoke("partial_possible"); }
    }

    private object ExecuteAndVerify(UIApplication app, Document document, UIDocument? uidoc, RequestRecord request)
    {
        var args = request.Admission.Arguments;
        var action = args.GetProperty("action");
        var checks = args.TryGetProperty("checks", out var c) ? c.EnumerateArray().ToArray() : [];
        if (args.TryGetProperty("preflights", out var legacy) && legacy.GetArrayLength() > 0)
            throw new RequestDispatchException("unsupported_preflights", "Use explicit checks: exists, bounds, count, parameter.");
        OptionalChecks.Validate(checks);
        var rollback = args.TryGetProperty("rollback_on_failure", out var r) && r.GetBoolean();
        if (rollback && request.Admission.TransactionMode is not ("auto" or "group"))
            throw new RequestDispatchException("rollback_requires_owned_transaction", "rollback_on_failure requires auto or group mode.");
        var tool = action.GetProperty("tool").GetString()!;
        var actionArgs = action.GetProperty("arguments");
        if (tool == "run_python")
            runtime.Providers.Prepare(request.Admission.ProviderGeneration!, actionArgs.GetProperty("source").GetString() ?? string.Empty);
        var beforeWarnings = document.GetWarnings().Select(VerificationService.WarningKey).ToHashSet();
        return _transactions.Execute(document, request.Admission.TransactionMode!, $"3XN-RevitMCP verify {request.Admission.Tool}", () =>
        {
            var result = DispatchRaw(app, document, uidoc, request.Admission.RequestId, tool, actionArgs, request.Admission.ProviderGeneration);
            if (document.IsModifiable) document.Regenerate();
            var verifyIds = args.TryGetProperty("element_ids", out var ids) ? ids.EnumerateArray().Select(x => new ElementId(x.GetInt64())).ToList() : new List<ElementId>();
            var projectedResult = JsonSerializer.SerializeToElement(result);
            var resultKey = args.TryGetProperty("result_ids_key", out var key) ? key.GetString() : "element_ids";
            if (!string.IsNullOrEmpty(resultKey) && projectedResult.ValueKind == JsonValueKind.Object && projectedResult.TryGetProperty(resultKey, out var resultIds) && resultIds.ValueKind == JsonValueKind.Array)
                verifyIds.AddRange(resultIds.EnumerateArray().Select(x => new ElementId(x.GetInt64())));
            var uniqueIds = verifyIds.Distinct().Take(1000).ToArray();
            var evaluated = OptionalChecks.Evaluate(document, checks, uniqueIds, out var passed);
            var elements = uniqueIds.Select(document.GetElement).Where(e => e is not null).Select(e => VerificationService.Element(document, e!, fields: VerificationService.Fields(args))).ToArray();
            var warningDelta = document.GetWarnings().Where(w => !beforeWarnings.Contains(VerificationService.WarningKey(w))).Take(100).Select(VerificationService.Warning).ToArray();
            var verification = new { checks = evaluated, passed = checks.Length > 0 ? (bool?)passed : null, elements,
                missing_ids = uniqueIds.Where(id => document.GetElement(id) is null).Select(id => id.Value).ToArray(),
                warning_delta_before_commit = warningDelta, note = "Commit-time failures and suppressed warnings are in the request receipt." };
            if (!passed && rollback) throw new VerificationFailedException(JsonSerializer.SerializeToElement(verification));
            return new { execution = result, verification };
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
        var includeTypes = args.TryGetProperty("include_types", out var types) && types.GetBoolean();
        if (!includeTypes) collector = collector.WhereElementIsNotElementType();
        else collector = collector.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true)));
        var afterId = args.TryGetProperty("after_id", out var cursor) ? cursor.GetInt64() : long.MinValue;
        var nameContains = args.TryGetProperty("name_contains", out var name) ? name.GetString() : null;
        var typeId = args.TryGetProperty("type_id", out var t) ? t.GetInt64() : (long?)null;
        var levelId = args.TryGetProperty("level_id", out var lv) ? lv.GetInt64() : (long?)null;
        var page = collector.Where(e => e.Id.Value > afterId
                && (typeId is null || e.GetTypeId().Value == typeId)
                && (levelId is null || e.LevelId.Value == levelId)
                && (nameContains is null || e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Id.Value).Take(limit + 1).ToArray();
        var fields = VerificationService.Fields(args) ?? ["identity", "relationships"];
        var elements = page.Take(limit).Select(e => VerificationService.Element(document, e, 20, fields)).ToArray();
        return new { elements, limit, has_more = page.Length > limit, next_after_id = page.Length > limit ? page[limit - 1].Id.Value : (long?)null,
            note = "Cursor is an element ID, not a frozen snapshot; restart if the document changes during pagination." };
    }
    private static object GetElements(Document document, JsonElement args)
    {
        var ids = Ids(args); var elements = ids.Select(document.GetElement).Where(e => e is not null).Take(1000).Select(e => VerificationService.Element(document, e!, fields: VerificationService.Fields(args))).ToArray();
        return new { elements, missing_ids = ids.Where(id => document.GetElement(id) is null).Select(id => id.Value).ToArray(), omitted_fields = Array.Empty<string>(), deferred_fields = VerificationService.DeferredFields };
    }
    private static object GetParameters(Document document, JsonElement args)
    {
        var ids = Ids(args);
        var parameterIds = args.TryGetProperty("parameter_ids", out var pids) ? pids.EnumerateArray().Select(p => p.GetInt64()).ToArray() : null;
        return new { elements = ids.Select(document.GetElement).Where(e => e is not null).Select(e =>
        {
            var count = e!.Parameters.Cast<Parameter>().Count(p => parameterIds is null || parameterIds.Contains(p.Id.Value));
            return new { element_id = e.Id.Value, parameters = VerificationService.Parameters(e, parameterIds), total_parameters = count, has_more = count > 500 };
        }).ToArray(), missing_ids = ids.Where(id => document.GetElement(id) is null).Select(id => id.Value).ToArray(), omitted_fields = Array.Empty<string>() };
    }
    private static object Select(UIDocument uidoc, JsonElement args) { var ids = Ids(args); uidoc.Selection.SetElementIds(ids); return new { selected_ids = ids.Select(x => x.Value).ToArray() }; }
    private static object Zoom(UIDocument uidoc, JsonElement args) { var ids = Ids(args); uidoc.ShowElements(ids); return new { zoomed_ids = ids.Select(x => x.Value).ToArray() }; }
    private static object OpenView(Document doc, UIDocument uidoc, JsonElement args) { var id = new ElementId(args.GetProperty("view_id").GetInt64()); var view = doc.GetElement(id) as View ?? throw new RequestDispatchException("view_not_found", "The requested view does not exist."); uidoc.RequestViewChange(view); return new { requested_view_id = id.Value, current_view_id = uidoc.ActiveView.Id.Value, confirmed = uidoc.ActiveView.Id == id, view_name = view.Name, note = "RequestViewChange completes after this handler exits; get_active_context confirms the displayed view." }; }
    private static object ExportView(Document doc, JsonElement args) => CaptureService.ExportExisting(doc, args);
    private static ElementId[] Ids(JsonElement args)
    {
        var values = args.GetProperty("element_ids");
        if (values.GetArrayLength() > 1000) throw new RequestDispatchException("element_limit", "Pass at most 1000 element IDs per call.");
        return values.EnumerateArray().Select(x => new ElementId(x.GetInt64())).ToArray();
    }
    private static object JsonValue(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
