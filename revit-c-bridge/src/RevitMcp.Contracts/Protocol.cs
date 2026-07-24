using System.Buffers.Binary;
using System.Text;

namespace RevitMcp.Contracts;

public static class ProtocolConstants
{
    public const string Version = "0.1";
    public const string RoslynAbi = "revit-mcp.roslyn/1";
    public const string PythonAbi = "revit-mcp.python/1";
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int MaxSourceBytes = 512 * 1024;
    public const int MaxResultBytes = 2 * 1024 * 1024;
}

public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class FrameCodec
{
    public static async ValueTask<byte[]> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maxBytes)
            throw new ProtocolException("invalid_frame_size", $"Frame size {length} is outside 1..{maxBytes}.");
        var body = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxBytes, CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maxBytes)
            throw new ProtocolException("invalid_frame_size", $"Frame size {payload.Length} is outside 1..{maxBytes}.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await WriteAllAsync(stream, header, cancellationToken).ConfigureAwait(false);
        await WriteAllAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Utf8(string json)
    {
        try { return new UTF8Encoding(false, true).GetBytes(json); }
        catch (EncoderFallbackException ex) { throw new ProtocolException("invalid_utf8", ex.Message); }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> target, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var count = await stream.ReadAsync(target[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Peer disconnected during a frame.");
            offset += count;
        }
    }

    private static async ValueTask WriteAllAsync(Stream stream, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        // Stream.WriteAsync is required to consume the supplied buffer or throw. Keeping this
        // helper explicit documents the matching exact-write contract used by Win32 clients.
        await stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ProtocolRequest(
    string ProtocolVersion,
    string RequestId,
    string Tool,
    string InstanceNonce,
    string? DocumentSession,
    long? DocumentGeneration,
    DateTimeOffset DeadlineUtc,
    string? IdempotencyKey,
    string? TransactionMode,
    System.Text.Json.JsonElement Arguments);

public sealed record ProtocolError(string Code, string Message, string? Remediation = null, bool Retryable = false);

public sealed record ProtocolResponse(
    string ProtocolVersion,
    string RequestId,
    string State,
    System.Text.Json.JsonElement? Result,
    ProtocolError? Error,
    string[] OmittedFields,
    string[] DeferredFields);

public sealed record CompileRequest(string Source, string RevitYear, string[] ReferencePaths, string CacheKeySeed);
public sealed record CompileResult(bool Success, byte[]? AssemblyBytes, byte[]? PdbBytes, string DiagnosticsJson, string CacheKey);

public interface IRoslynCompilerProvider
{
    string AbiVersion { get; }
    string ProviderVersion { get; }
    CompileResult Compile(CompileRequest request);
}
