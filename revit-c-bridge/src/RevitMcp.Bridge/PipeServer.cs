using RevitMcp.Contracts;
using RevitMcp.Core;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed class PipeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly BridgeRuntime _runtime;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public PipeServer(BridgeRuntime runtime) => _runtime = runtime;
    public void Start() => _listener = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_runtime.PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connected), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                server?.Dispose();
                _runtime.Log.Add(new(DateTimeOffset.UtcNow, null, null, "listener_error", null, null, null, null, null, RevitMcp.Core.Redaction.Error(ex)));
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream)
    {
        using (stream)
        {
            while (stream.IsConnected && !_stop.IsCancellationRequested)
            {
                byte[] frame;
                try { frame = await FrameCodec.ReadAsync(stream, ProtocolConstants.MaxFrameBytes, _stop.Token).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await TryWriteAsync(stream, Error("unknown", "malformed_frame", ex.Message)).ConfigureAwait(false); break;
                }

                ProtocolResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<ProtocolRequest>(frame, JsonOptions) ?? throw new ProtocolException("invalid_json", "Request was null.");
                    response = await DispatchAsync(request, frame.Length, _stop.Token).ConfigureAwait(false);
                }
                catch (JsonException ex) { response = Error("unknown", "invalid_json", ex.Message); }
                catch (Exception ex) { response = Error("unknown", "bridge_error", RevitMcp.Core.Redaction.Error(ex)); }
                if (!await TryWriteAsync(stream, response).ConfigureAwait(false)) break;
            }
        }
    }

    private async Task<ProtocolResponse> DispatchAsync(ProtocolRequest request, int inputBytes, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != ProtocolConstants.Version) return Error(request.RequestId, "protocol_mismatch", $"Expected {ProtocolConstants.Version}.");
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.InstanceNonce), System.Text.Encoding.UTF8.GetBytes(_runtime.InstanceNonce)))
            return Error(request.RequestId, "invalid_instance_nonce", "Discovery nonce does not match this Revit process.");
        if (!_runtime.AdmissionEnabled) return Error(request.RequestId, "bridge_off", "Use the Bridge ON ribbon control.");

        var fast = FastPath(request);
        if (fast is not null) return fast;
        var modeError = RequestValidation.ValidateTransactionMode(request.Tool, request.TransactionMode);
        if (modeError is not null) return Error(request.RequestId, modeError, "run_python and run_csharp require read, auto, manual, or group.");
        if (request.DeadlineUtc <= DateTimeOffset.UtcNow) return Error(request.RequestId, "expired_before_admission", "Deadline has already passed.");
        JsonElement source = default;
        if (request.Tool is "run_python" or "run_csharp")
        {
            if (!request.Arguments.TryGetProperty("source", out source)) return Error(request.RequestId, "source_required", "arguments.source is required.");
            if (source.ValueKind != JsonValueKind.String) return Error(request.RequestId, "source_invalid", "arguments.source must be a string.");
            if (System.Text.Encoding.UTF8.GetByteCount(source.GetString() ?? "") > ProtocolConstants.MaxSourceBytes)
                return Error(request.RequestId, "source_too_large", $"Source limit is {ProtocolConstants.MaxSourceBytes} bytes.");
        }

        var settings = LocalSettingsStore.Load();
        if (!settings.AllowArbitraryCode)
        {
            foreach (var (dynamicTool, dynamicSource) in DynamicSources(request))
            {
                if (CodeGate.IsVetted(settings, dynamicTool, dynamicSource)) continue;
                return Error(request.RequestId, "arbitrary_code_disabled",
                    "Arbitrary code is disabled on this machine; only enabled saved tools may run. Use run_saved_tool, or an operator can enable 'Allow arbitrary code' in Revit MCP Settings on the ribbon.");
            }
        }

        if (request.Tool == "run_csharp")
        {
            var prepared = _runtime.Roslyn.Prepare(request.RequestId, source.GetString() ?? string.Empty);
            if (!prepared.Success) return new(ProtocolConstants.Version, request.RequestId, "failed", null, new("csharp_compile_error", prepared.DiagnosticsJson, "Fix diagnostics mapped to agent.cs."), [], []);
        }

        if (request.Tool == "execute_batch" && request.Arguments.TryGetProperty("steps", out var batchSteps))
        {
            var index = 0;
            foreach (var step in batchSteps.EnumerateArray())
            {
                if (step.GetProperty("tool").GetString() == "run_csharp")
                {
                    var nestedSource = step.GetProperty("arguments").GetProperty("source").GetString() ?? string.Empty;
                    var prepared = _runtime.Roslyn.Prepare(request.RequestId + ":" + index, nestedSource);
                    if (!prepared.Success) return new(ProtocolConstants.Version, request.RequestId, "failed", null, new("csharp_compile_error", prepared.DiagnosticsJson, $"Fix diagnostics for batch step {index}."), [], []);
                }
                index++;
            }
        }
        if (request.Tool == "execute_and_verify" && request.Arguments.TryGetProperty("action", out var action) && action.GetProperty("tool").GetString() == "run_csharp")
        {
            var nestedSource = action.GetProperty("arguments").GetProperty("source").GetString() ?? string.Empty;
            var prepared = _runtime.Roslyn.Prepare(request.RequestId, nestedSource);
            if (!prepared.Success) return new(ProtocolConstants.Version, request.RequestId, "failed", null, new("csharp_compile_error", prepared.DiagnosticsJson, "Fix diagnostics mapped to agent.cs."), [], []);
        }

        var usesPython = request.Tool == "run_python"
            || (request.Tool == "execute_batch" && request.Arguments.TryGetProperty("steps", out var providerSteps) && providerSteps.EnumerateArray().Any(step => step.GetProperty("tool").GetString() == "run_python"))
            || (request.Tool == "execute_and_verify" && request.Arguments.TryGetProperty("action", out var providerAction) && providerAction.GetProperty("tool").GetString() == "run_python");
        var providerGeneration = usesPython ? _runtime.Providers.CurrentGeneration : null;
        if (usesPython && _runtime.Providers.Capability != "available")
            return Error(request.RequestId, "capability_unavailable", $"Python provider is {_runtime.Providers.Capability}; use Python ON after pyRevit registration.");

        var admission = new RequestAdmission(request.RequestId, request.IdempotencyKey, request.Tool, request.DocumentSession,
            request.DocumentGeneration, request.DeadlineUtc, providerGeneration, request.TransactionMode, request.Arguments, inputBytes);
        var (record, created) = _runtime.Ledger.Admit(admission);
        if (created)
        {
            if (!_runtime.Queue.TryEnqueue(record))
            {
                record.Transition(RequestState.Failed, errorCode: "queue_full", redactedError: "Bounded Revit queue is full; no mutation was admitted.");
                return RecordResponse(record);
            }
            _runtime.Log.Add(new(DateTimeOffset.UtcNow, request.RequestId, request.DocumentSession, "admitted", request.Tool, "queued", inputBytes, null, providerGeneration, null, request.TransactionMode, null, AdmittedLabel(request.Arguments)));
            _runtime.NotifyWork();
        }

        while (!record.State.IsTerminal() && DateTimeOffset.UtcNow < request.DeadlineUtc && !cancellationToken.IsCancellationRequested)
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        return RecordResponse(record);
    }

    private static string? AdmittedLabel(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("label", out var label)
        && label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } text
            ? text[..Math.Min(text.Length, 120)]
            : null;

    // Every shape that can carry agent-authored source: direct dynamic calls,
    // batch steps, and the nested execute_and_verify action.
    private static IEnumerable<(string Tool, string Source)> DynamicSources(ProtocolRequest request)
    {
        if (request.Tool is "run_python" or "run_csharp")
        {
            if (request.Arguments.TryGetProperty("source", out var direct) && direct.ValueKind == JsonValueKind.String)
                yield return (request.Tool, direct.GetString()!);
            yield break;
        }
        if (request.Tool == "execute_batch" && request.Arguments.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("tool", out var stepTool) || stepTool.GetString() is not ("run_python" or "run_csharp")) continue;
                if (step.TryGetProperty("arguments", out var stepArgs) && stepArgs.TryGetProperty("source", out var stepSource) && stepSource.ValueKind == JsonValueKind.String)
                    yield return (stepTool.GetString()!, stepSource.GetString()!);
            }
            yield break;
        }
        if (request.Tool == "execute_and_verify" && request.Arguments.TryGetProperty("action", out var action)
            && action.TryGetProperty("tool", out var actionTool) && actionTool.GetString() is ("run_python" or "run_csharp")
            && action.TryGetProperty("arguments", out var actionArgs) && actionArgs.TryGetProperty("source", out var actionSource) && actionSource.ValueKind == JsonValueKind.String)
            yield return (actionTool.GetString()!, actionSource.GetString()!);
    }

    private ProtocolResponse? FastPath(ProtocolRequest request)
    {
        if (request.Tool == "get_request_status")
        {
            if (!request.Arguments.TryGetProperty("request_id", out var statusId) || !_runtime.Ledger.TryGet(statusId.GetString() ?? "", out var statusRecord))
                return Error(request.RequestId, "request_not_found", "No ledger record exists for that request ID.");
            return Success(request.RequestId, RecordObject(statusRecord!));
        }
        object? value = request.Tool switch
        {
            "get_capabilities" => _runtime.Capabilities(),
            "get_logs_tail" => new { entries = _runtime.Log.Tail(request.Arguments.TryGetProperty("count", out var c) ? c.GetInt32() : 100), omitted_fields = Array.Empty<string>() },
            "list_revit_instances" => new { instances = new[] { new { pid = Environment.ProcessId, revit_year = _runtime.RevitYear, pipe_name = _runtime.PipeName, bridge_state = "on", instance_nonce = _runtime.InstanceNonce } } },
            _ => null
        };
        return value is null ? null : Success(request.RequestId, value);
    }

    private static object RecordObject(RequestRecord record) => new
    {
        request_id = record.Admission.RequestId,
        tool = record.Admission.Tool,
        state = record.State.ToString().ToLowerInvariant(),
        accepted_utc = record.AcceptedUtc,
        updated_utc = record.UpdatedUtc,
        result = record.Result,
        error_code = record.ErrorCode,
        error = record.RedactedError
    };

    private static ProtocolResponse RecordResponse(RequestRecord record) => new(ProtocolConstants.Version, record.Admission.RequestId,
        record.State.ToString().ToLowerInvariant(), record.Result, record.ErrorCode is null ? null : new(record.ErrorCode, record.RedactedError ?? record.ErrorCode), [], []);
    private static ProtocolResponse Success(string id, object value) => new(ProtocolConstants.Version, id, "succeeded", JsonSerializer.SerializeToElement(value, JsonOptions), null, [], []);
    private static ProtocolResponse Error(string id, string code, string message, bool retryable = false) => new(ProtocolConstants.Version, id, "failed", null, new(code, message, null, retryable), [], []);
    private static async Task<bool> TryWriteAsync(Stream stream, ProtocolResponse response)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            await FrameCodec.WriteAsync(stream, bytes, ProtocolConstants.MaxFrameBytes, default).ConfigureAwait(false); return true;
        }
        catch { return false; }
    }
    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop.Dispose();
    }
}
