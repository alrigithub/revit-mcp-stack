using Autodesk.Revit.DB;
using RevitMcp.Bridge;
using System.Text.Json;

internal static class HardeningTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Hardening assertion failed."); }
    private static void Fails(string code, Action action)
    {
        try { action(); }
        catch (RequestDispatchException ex) { Check(ex.Code == code); return; }
        throw new Exception("Expected " + code);
    }

    public static Task OversizedAutoRollsBack()
    {
        foreach (var mode in new[] { "auto", "group" })
        {
            var doc = new Document();
            Fails("result_too_large", () => new TransactionCoordinator().Execute(doc, mode, "test", () =>
            {
                doc.Value = 1;
                return Enumerable.Repeat(new string('x', 4096), 600).ToArray();
            }));
            Check(doc.Value == 0 && doc.Commits == 0 && !doc.IsModifiable);
        }
        return Task.CompletedTask;
    }

    public static Task AggregateBatchRollsBack()
    {
        var doc = new Document();
        Func<object> step = () => { doc.Value++; return Enumerable.Repeat(new string('x', 4096), 300).ToArray(); };
        Fails("result_too_large", () => new TransactionCoordinator().ExecuteAtomicBatch(doc, "test", [step, step]));
        Check(doc.Value == 0 && !doc.IsModifiable);
        return Task.CompletedTask;
    }

    public static Task CommittedProjectionIsStable()
    {
        var doc = new Document();
        var result = new TransactionCoordinator().Execute(doc, "auto", "test", () =>
        {
            doc.Value++;
            return new { items = Enumerable.Range(0, 1001).ToArray(), text = new string('x', 5000) };
        });
        var response = ResultProjection.Prepare(result);
        Check(doc.Value == 1 && doc.Commits == 1);
        Check(response.GetProperty("value").GetProperty("items").GetArrayLength() == 1000);
        Check(response.GetProperty("omitted_fields").GetArrayLength() == 2);
        return Task.CompletedTask;
    }

    public static Task RejectedCommitFails()
    {
        var doc = new Document { RejectCommit = true };
        Fails("transaction_commit_failed", () => new TransactionCoordinator().Execute(doc, "auto", "test", () => { doc.Value++; return new { ok = true }; }));
        Check(doc.Value == 0 && !doc.IsModifiable);
        return Task.CompletedTask;
    }

    public static Task GateHonorsOwnership()
    {
        using var files = new ToolFiles();
        files.Write(files.Primary, "_result = 1");
        files.Write(files.Secondary, "_result = 2");
        var settings = new LocalSettings(files.Primary, [files.Secondary], []);
        Check(CodeGate.IsVetted(settings, "run_python", "_result = 1"));
        Check(!CodeGate.IsVetted(settings, "run_python", "_result = 2"));
        File.WriteAllText(Path.Combine(files.Primary, "tool.disabled"), "");
        Check(!CodeGate.IsVetted(settings, "run_python", "_result = 1"));
        Check(!CodeGate.IsVetted(settings, "run_python", "_result = 2"));
        File.Delete(Path.Combine(files.Primary, "tool.disabled"));
        Check(!CodeGate.IsVetted(settings with { DisabledToolPaths = [files.Primary] }, "run_python", "_result = 2"));
        File.WriteAllText(Path.Combine(files.Primary, "tool.json"), "invalid");
        Check(!CodeGate.IsVetted(settings, "run_python", "_result = 2"));
        return Task.CompletedTask;
    }

    public static Task GateValidatesManifest()
    {
        using var files = new ToolFiles();
        var settings = new LocalSettings(files.Primary, [], []);
        foreach (var invalid in new[] { "invalid", "{}", "[]", ToolFiles.Manifest.Replace("\"python\"", "\"csharp\""), ToolFiles.Manifest.Replace("\"read\"", "\"invalid\""), ToolFiles.Manifest.Replace("\"params\":[]", "\"params\":[{}]") })
        {
            files.Write(files.Primary, "_result = 1");
            File.WriteAllText(Path.Combine(files.Primary, "tool.json"), invalid);
            Check(!CodeGate.IsVetted(settings, "run_python", "_result = 1"));
        }
        files.Write(files.Primary, "_result = 1\r\n");
        Check(CodeGate.IsVetted(settings, "run_python", "_result = 1\n"));
        Check(!CodeGate.IsVetted(settings with { Error = "bad settings" }, "run_python", "_result = 1"));
        return Task.CompletedTask;
    }

    private sealed class ToolFiles : IDisposable
    {
        public const string Manifest = "{\"manifest_version\":1,\"name\":\"tool\",\"description\":\"test\",\"engine\":\"python\",\"transaction_mode\":\"read\",\"params\":[]}";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "revit-hardening-" + Guid.NewGuid().ToString("N"));
        public string Primary => Path.Combine(_root, "primary");
        public string Secondary => Path.Combine(_root, "secondary");
        public ToolFiles() { Directory.CreateDirectory(Primary); Directory.CreateDirectory(Secondary); }
        public void Write(string root, string source) { File.WriteAllText(Path.Combine(root, "tool.json"), Manifest); File.WriteAllText(Path.Combine(root, "tool.py"), source); }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
