using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aperture.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<int> Run(string[] args)
    {
        string root = Path.GetFullPath(Value(args, "--root") ?? Environment.CurrentDirectory);
        if (args.Contains("--self-test")) { await SelfTest(root); return 0; }
        bool protocolOnly = args.Contains("--protocol-only");
        bool modelSmoke = args.Contains("--model-smoke");
        string context = Value(args, "--context");
        if (!protocolOnly && context == null) throw new ArgumentException("Use --context <target.json> or --protocol-only.");
        if (modelSmoke)
        {
            Require(!protocolOnly && context != null, "--model-smoke requires a disposable --context and cannot use --protocol-only.");
            string disposableRoot = Path.GetFullPath(Path.Combine(root, ".local", "smoke")) + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(context).StartsWith(disposableRoot, StringComparison.OrdinalIgnoreCase),
                "Model smoke accepts only contexts generated under this repository's .local/smoke. Never pass a user workbook context.");
        }
        ExcelTarget target = context == null ? null : JsonConvert.DeserializeObject<ExcelTarget>(File.ReadAllText(context));
        var options = new PiSessionOptions { RepositoryRoot = root, EnableExcelTools = !protocolOnly };
        if (modelSmoke) { options.Provider = "openrouter"; options.Model = "openrouter/free"; }
        using (var session = await PiSession.StartAsync(options, target))
        {
            JObject status = null;
            var notices = new List<string>();
            session.StandardError += (sender, e) => Console.Error.WriteLine(e.Message);
            session.EventReceived += (sender, e) =>
            {
                if (e.Type != "extension_ui_request" || (string)e.Json["method"] != "notify") return;
                string text = (string)e.Json["message"] ?? "";
                if (text.StartsWith("APERTURE_STATUS ", StringComparison.Ordinal)) status = JObject.Parse(text.Substring(16));
                else notices.Add(text);
            };
            JObject state = await session.GetStateAsync();
            if (!protocolOnly) await session.SendPromptAsync("/mcp");
            await session.SendPromptAsync("/aperture-status");
            Require(status != null, "Pi did not report its actual tool catalog.");
            var active = status["activeTools"].Values<string>().ToArray();
            var excel = status["excelTools"].Values<string>().ToArray();
            Require(active.All(t => t.StartsWith("mcp__aperture_excel__", StringComparison.Ordinal)), "Unexpected non-Excel tool was enabled.");
            if (!protocolOnly) Require(excel.Length > 0 && active.Length > 0, "No live Excel MCP tools loaded. " + string.Join("; ", notices));
            JObject modelResult = modelSmoke ? await RunModelSmoke(session, target) : null;
            int pid = session.ProcessId;
            await session.StopAsync();
            Require(!ProcessExists(pid), "Pi process survived shutdown.");
            Console.WriteLine(new JObject
            {
                ["ok"] = modelResult == null || (bool)modelResult["ok"], ["protocolOnly"] = protocolOnly, ["rpcGetState"] = true,
                ["sessionId"] = state["data"]?["sessionId"], ["modelSelected"] = state["data"]?["model"] != null,
                ["activeTools"] = new JArray(active), ["excelTools"] = new JArray(excel), ["notices"] = new JArray(notices),
                ["cleanShutdown"] = true, ["modelRequestMade"] = modelSmoke,
                ["modelSmoke"] = modelResult
            }.ToString(Formatting.Indented));
            if (modelResult != null && !(bool)modelResult["ok"]) return 1;
        }
        return 0;
    }

    private static async Task<JObject> RunModelSmoke(PiSession session, ExcelTarget target)
    {
        // This opt-in prompt runs only against the harness-created disposable book.
        // No model/global config is modified, and there is no paid-model fallback.
        const string prefix = "mcp__aperture_excel__";
        var gate = new object();
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new Dictionary<string, JObject>();
        var completed = new List<string>();
        var errors = new List<string>();
        JObject observedTarget = null, observedRange = null;
        string finalText = "";
        bool aborted = false;
        var watch = Stopwatch.StartNew();
        EventHandler<PiEvent> collector = (sender, e) =>
        {
            lock (gate)
            {
                try
                {
                    if (e.Type == "tool_execution_start")
                    {
                        string name = (string)e.Json["toolName"] ?? "";
                        starts[(string)e.Json["toolCallId"]] = (JObject)e.Json.DeepClone();
                        if (name != prefix + "excel_target" && name != prefix + "excel_read_range")
                            errors.Add("Model attempted an unexpected tool: " + name);
                    }
                    else if (e.Type == "tool_execution_end")
                    {
                        string name = (string)e.Json["toolName"] ?? "";
                        if ((bool?)e.Json["isError"] == true) errors.Add("Model tool returned an error: " + name);
                        else
                        {
                            completed.Add(name);
                            JObject data = McpData(e.Json["result"]);
                            if (name == prefix + "excel_target") observedTarget = data;
                            if (name == prefix + "excel_read_range")
                            {
                                JObject start;
                                Require(starts.TryGetValue((string)e.Json["toolCallId"], out start), "Read result has no matching invocation.");
                                var toolArgs = start["args"];
                                Require((string)toolArgs?["excelInstanceId"] == target.ExcelInstanceId
                                    && (string)toolArgs?["workbookId"] == target.WorkbookId
                                    && (string)toolArgs?["sheet"] == "ApertureSmoke"
                                    && (string)toolArgs?["address"] == "A1:B2", "Model read did not use the exact disposable binding/range.");
                                observedRange = data;
                            }
                        }
                    }
                    else if (e.Type == "message_end" && (string)e.Json["message"]?["role"] == "assistant")
                    {
                        var message = e.Json["message"];
                        string error = (string)message["errorMessage"];
                        if (!string.IsNullOrEmpty(error)) errors.Add("Provider/assistant error: " + error);
                        if ((string)message["stopReason"] == "error" || (string)message["stopReason"] == "aborted")
                            errors.Add("Assistant stopped with: " + (string)message["stopReason"]);
                        var parts = message["content"] as JArray;
                        if (parts != null)
                        {
                            string text = string.Concat(parts.Where(p => (string)p["type"] == "text").Select(p => (string)p["text"]));
                            if (!string.IsNullOrWhiteSpace(text)) finalText = text;
                        }
                    }
                    else if (e.Type == "agent_settled")
                    {
                        aborted = (bool?)e.Json["aborted"] == true;
                        settled.TrySetResult(true);
                    }
                    else if (e.Type == "process_exit") settled.TrySetException(new IOException("Pi exited before the model run settled."));
                }
                catch (Exception error) { errors.Add("Event validation failed: " + error.Message); }
            }
        };
        session.EventReceived += collector;
        try
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                await session.SendPromptAsync(
                    "This is a read-only integration check on a disposable workbook. " +
                    "Call excel_target, then excel_read_range using exactly the returned excelInstanceId and workbookId, " +
                    "sheet ApertureSmoke and address A1:B2. Use only those two tools. " +
                    "Do not modify, create, format, calculate, save, close or delete anything. " +
                    "Return one JSON object with computedB1 set to the numeric live value in B1 and formulaB1 set to the exact formula in B1. " +
                    "Use the tools to obtain both facts; do not guess.", deadline.Token);
                Task winner = await Task.WhenAny(settled.Task, Task.Delay(Timeout.Infinite, deadline.Token));
                if (winner != settled.Task) throw new TimeoutException("Model smoke exceeded its 90-second deadline.");
                await settled.Task;
            }
            lock (gate)
            {
                Require(!aborted, "Model run was aborted.");
                Require(errors.Count == 0, string.Join("; ", errors));
                Require(observedTarget != null && (string)observedTarget["workbookId"] == target.WorkbookId,
                    "Model did not complete excel_target against the captured workbook.");
                Require(observedRange != null && (double?)observedRange["values"]?[0]?[1] == 42
                    && (string)observedRange["formulas"]?[0]?[1] == "=SUM(A1:A2)",
                    "Model did not read live B1=42 and its expected formula through MCP.");
                int firstBrace = finalText.IndexOf('{'), lastBrace = finalText.LastIndexOf('}');
                Require(firstBrace >= 0 && lastBrace > firstBrace, "Assistant did not return a JSON result.");
                JObject answer = JObject.Parse(finalText.Substring(firstBrace, lastBrace - firstBrace + 1));
                Require((double?)answer["computedB1"] == 42 && (string)answer["formulaB1"] == "=SUM(A1:A2)",
                    "Assistant answer did not report the live computed value and formula.");
            }
        }
        catch (Exception error)
        {
            lock (gate) errors.Add(error is OperationCanceledException ? "Model smoke exceeded its 90-second deadline." : error.Message);
            // Abort waits for Pi idle; bound it too, then the caller closes its job.
            try
            {
                using (var abortDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    await session.AbortAsync(abortDeadline.Token);
            }
            catch { }
        }
        finally { session.EventReceived -= collector; }
        lock (gate)
        {
            return new JObject
            {
                ["ok"] = errors.Count == 0, ["provider"] = "openrouter", ["model"] = "openrouter/free",
                ["modelRequestMade"] = true, ["agentSettled"] = settled.Task.Status == TaskStatus.RanToCompletion,
                ["elapsedMs"] = watch.ElapsedMilliseconds, ["completedTools"] = new JArray(completed),
                ["computedB1"] = observedRange?["values"]?[0]?[1], ["formulaB1"] = observedRange?["formulas"]?[0]?[1],
                ["assistantText"] = finalText, ["errors"] = new JArray(errors.Distinct())
            };
        }
    }

    private static JObject McpData(JToken result)
    {
        // Pi wraps the raw MCP CallToolResult in its own structuredContent.
        var raw = result?["structuredContent"];
        if (raw?["structuredContent"] is JObject data) return data;
        var blocks = raw?["content"] as JArray ?? result?["content"] as JArray;
        if (blocks != null)
            foreach (var block in blocks)
                if ((string)block["type"] == "text")
                {
                    try { return JObject.Parse((string)block["text"]); }
                    catch (JsonException) { }
                }
        return null;
    }

    private static async Task SelfTest(string root)
    {
        string node = FindNode();
        string directory = Path.Combine(root, ".local", "runtime-tests");
        Directory.CreateDirectory(directory);
        var config = new SessionConfiguration
        {
            Node = node, Directory = directory,
            Arguments = SessionConfiguration.Quote(Path.Combine(root, "src", "Aperture.Runtime", "pi-launcher.mjs")) + " " +
                SessionConfiguration.Quote(Path.Combine(root, "tests", "runtime", "protocol-fixture.mjs"))
        };
        int childPid;
        using (var session = await PiSession.StartPreparedAsync(config, 1))
        {
            JObject initial = await session.GetStateAsync();
            childPid = (int)initial["data"]["childPid"];
            Task<JObject> slow = session.SendCommandAsync(new JObject { ["type"] = "get_state", ["tag"] = "slow", ["delay"] = 70 });
            Task<JObject> fast = session.SendCommandAsync(new JObject { ["type"] = "get_state", ["tag"] = "fast", ["delay"] = 5 });
            JObject[] responses = await Task.WhenAll(slow, fast);
            Require((string)responses[0]["data"]["tag"] == "slow" && (string)responses[1]["data"]["tag"] == "fast", "Response correlation failed.");
            var text = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.EventReceived += (sender, e) => { if (e.Type == "message_update") text.TrySetResult((string)e.Json["assistantMessageEvent"]["delta"]); };
            await session.SendPromptAsync("stream");
            Require(await text.Task == "Привет\u2028мир\u2029!", "Fragmented UTF-8/Unicode JSON framing failed.");
            bool rejection = false;
            try { await session.SendCommandAsync(new JObject { ["type"] = "get_messages" }); }
            catch (InvalidOperationException error) when (error.Message == "fixture rejection") { rejection = true; }
            Require(rejection, "RPC rejection was swallowed.");
            bool timeout = false;
            try { await session.SendPromptAsync("timeout"); } catch (TimeoutException) { timeout = true; }
            Require(timeout, "RPC deadline did not fire.");
            await session.GetStateAsync();
            await session.StopAsync();
        }
        for (int i = 0; i < 30 && ProcessExists(childPid); i++) await Task.Delay(100);
        Require(!ProcessExists(childPid), "Owned child survived the Windows job cleanup.");
        Console.WriteLine("{\"ok\":true,\"checks\":[\"response_correlation\",\"fragmented_utf8_unicode\",\"rpc_rejection\",\"timeout_then_recovery\",\"child_process_cleanup\"]}");
    }

    private static bool ProcessExists(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static string FindNode()
    {
        foreach (string item in Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator))
        { string path = Path.Combine(item.Trim('"'), "node.exe"); if (File.Exists(path)) return path; }
        throw new FileNotFoundException("node.exe not found");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Value(string[] args, string key) { int index = Array.IndexOf(args, key); return index < 0 ? null : args[index + 1]; }
}
