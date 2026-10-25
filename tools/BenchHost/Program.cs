using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aperture.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Headless benchmark host: one prompt, one agent run through the same PiSession and
// SessionConfiguration as the Excel pane, and run records in --run-dir (benchmark
// harness contract §4). The model never sees or writes these records.
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        BenchOptions options;
        RunRecorder recorder;
        try
        {
            options = BenchOptions.Parse(args);
            recorder = RunRecorder.Create(options.RunDirectory);
        }
        catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(BenchOptions.Usage);
            return ExitCodes.Usage;
        }
        using (recorder)
        using (var run = new BenchRun(options, recorder))
        {
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; run.Cancel(); };
            return run.RunAsync().GetAwaiter().GetResult();
        }
    }
}

internal static class ExitCodes
{
    public const int Done = 0, Failed = 1, Limit = 2, Cancelled = 3, Usage = 64;
    public static int For(string state) => state == "done" ? Done : state == "limit" ? Limit : state == "cancelled" ? Cancelled : Failed;
}

internal sealed class BenchOptions
{
    public const string Usage =
        "Usage: BenchHost --context <target.json> --prompt-file <task.md> --run-dir <dir> --timeout-s <n>\n" +
        "                 [--provider <p> --model <m>] [--thinking <level>] [--api-key-env <NAME>]\n" +
        "                 [--cost-currency <code>] [--root <repo>] [--bench]";
    private static readonly string[] ValueOptions = { "--root", "--context", "--provider", "--model", "--thinking", "--prompt-file", "--run-dir", "--timeout-s", "--api-key-env", "--cost-currency" };
    private static readonly string[] ThinkingLevels = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

    public string Root, Context, Provider, Model, Thinking, PromptFile, RunDirectory, ApiKeyEnv, CostCurrency;
    public int TimeoutSeconds;
    public bool Bench;

    public static BenchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new BenchOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bench") { options.Bench = true; continue; }
            if (!ValueOptions.Contains(args[i])) throw new ArgumentException("Unknown argument: " + args[i]);
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(args[i] + " requires a value.");
            values[args[i]] = args[++i];
        }
        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        options.Context = Get("--context") ?? throw new ArgumentException("--context <target.json> is required.");
        options.PromptFile = Get("--prompt-file") ?? throw new ArgumentException("--prompt-file is required.");
        options.RunDirectory = Get("--run-dir") ?? throw new ArgumentException("--run-dir is required.");
        if (!int.TryParse(Get("--timeout-s"), NumberStyles.None, CultureInfo.InvariantCulture, out options.TimeoutSeconds) || options.TimeoutSeconds <= 0)
            throw new ArgumentException("--timeout-s <positive seconds> is required.");
        options.Provider = Get("--provider");
        options.Model = Get("--model");
        if (options.Provider != null && options.Model == null) throw new ArgumentException("--provider requires --model.");
        options.Thinking = Get("--thinking");
        if (options.Thinking != null && !ThinkingLevels.Contains(options.Thinking)) throw new ArgumentException("--thinking must be one of: " + string.Join(", ", ThinkingLevels));
        options.ApiKeyEnv = Get("--api-key-env");
        if (options.ApiKeyEnv != null && !Regex.IsMatch(options.ApiKeyEnv, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("--api-key-env takes an environment variable name, never a key.");
        options.CostCurrency = Get("--cost-currency");
        options.Root = Path.GetFullPath(Get("--root") ?? DiscoverRoot());
        return options;
    }

    private static string DiscoverRoot()
    {
        for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "external", "pi")) && Directory.Exists(Path.Combine(directory.FullName, "src", "Aperture.Runtime")))
                return directory.FullName;
        throw new ArgumentException("Could not find the Aperture2 repository above BenchHost.exe; pass --root.");
    }
}

/// <summary>Owns the run_dir files. JSONL appends are flushed per record; JSON files are replaced atomically.</summary>
internal sealed class RunRecorder : IDisposable
{
    private static readonly string[] Owned = { "harness_info.json", "trajectory.jsonl", "pi_events.jsonl", "pi_stderr.log", "usage.json", "done.json" };
    private readonly object gate = new object();
    private readonly StreamWriter events, trajectory, stderr;
    public string Directory { get; }

    public static RunRecorder Create(string directory)
    {
        directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(directory);
        foreach (string name in Owned)
            if (File.Exists(Path.Combine(directory, name)))
                throw new InvalidOperationException("Run directory already contains " + name + "; use a new --run-dir for every run.");
        return new RunRecorder(directory);
    }

    private RunRecorder(string directory)
    {
        Directory = directory;
        events = Open("pi_events.jsonl");
        trajectory = Open("trajectory.jsonl");
        stderr = Open("pi_stderr.log");
    }

    private StreamWriter Open(string name) =>
        new StreamWriter(new FileStream(Path.Combine(Directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        { AutoFlush = true, NewLine = "\n" };

    public void PiEvent(JObject json) { lock (gate) events.WriteLine(json.ToString(Formatting.None)); }
    public void Trajectory(JObject record) { lock (gate) trajectory.WriteLine(record.ToString(Formatting.None)); }
    public void Diagnostic(string line) { lock (gate) stderr.WriteLine(line); }
    public bool Exists(string name) => File.Exists(Path.Combine(Directory, name));

    public void WriteJson(string name, JObject value)
    {
        // A reader never sees a partial file; done.json is the runner's completion signal.
        string path = Path.Combine(Directory, name), temporary = path + ".tmp";
        File.WriteAllText(temporary, value.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    public void Dispose() { lock (gate) { events.Dispose(); trajectory.Dispose(); stderr.Dispose(); } }
}

internal sealed class BenchRun : IDisposable
{
    private const string ExcelPrefix = "mcp__aperture_excel__";
    private sealed class ToolStart { public string Name; public JToken Args; public int Step; public long StartedMs; }

    private readonly BenchOptions options;
    private readonly RunRecorder recorder;
    private readonly object gate = new object();
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly DateTime startedAt = DateTime.UtcNow;
    private readonly CancellationTokenSource cancel = new CancellationTokenSource();
    private readonly TaskCompletionSource<JObject> settled = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<JObject> status = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, ToolStart> openTools = new Dictionary<string, ToolStart>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> modelsReturned = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, JObject> providerUsage = new Dictionary<string, JObject>(StringComparer.Ordinal);
    private readonly List<string> notices = new List<string>();
    private PiSession session;
    private JObject selectedModel;
    private bool promptSent, costKnown;
    private int step, llmCalls, llmCallsWithUsage, llmErrors, toolCalls, toolErrors, providerCostCalls;
    private long tokPrompt, tokCompletion, tokCached, tokCacheWrite, tokReasoning, tokTotal, llmTimeMs, toolTimeMs, messageStartedMs = -1;
    private double costInput, costOutput, costCacheRead, costCacheWrite, costTotal, providerCostTotal;
    private JObject lastAssistant;
    private string finalText = "";

    public BenchRun(BenchOptions options, RunRecorder recorder) { this.options = options; this.recorder = recorder; }
    public void Cancel() { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }

    public async Task<int> RunAsync()
    {
        string state = "failed", error = null, prompt = null, keySource = null;
        ExcelTarget target = null;
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds)))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancel.Token))
        {
            try
            {
                prompt = ReadPrompt();
                target = LoadTarget();
                keySource = EnsureApiKey();
                var sessionOptions = new PiSessionOptions
                {
                    RepositoryRoot = options.Root, EnableExcelTools = true,
                    Provider = options.Provider, Model = options.Model, Thinking = options.Thinking,
                    StateRoot = Path.Combine(recorder.Directory, "pi_state"), RecordProviderUsage = true
                };
                session = await PiSession.StartAsync(sessionOptions, target, stop.Token);
                session.EventReceived += OnEvent;
                session.StandardError += (sender, e) => recorder.Diagnostic(e.Message);
                var piState = (JObject)(await session.GetStateAsync(stop.Token))["data"];
                CheckSelectedModel(piState);
                // Both are Pi extension commands handled without a model request or a
                // transcript entry: wait for MCP initialization, then read the tool catalog.
                await session.SendPromptAsync("/mcp", stop.Token);
                await session.SendPromptAsync("/aperture-status", stop.Token);
                JObject catalog = await AwaitAsync(status.Task, stop.Token, TimeSpan.FromSeconds(10), "Pi did not report its tool catalog.");
                CheckTools(catalog);
                WriteHarnessInfo(prompt, target, keySource, piState, catalog, null);
                lock (gate) promptSent = true;
                JObject accepted = await session.SendPromptAsync(prompt, stop.Token);
                string disposition = (string)accepted["data"]?["disposition"];
                if (disposition != "started") throw new InvalidOperationException("Pi did not start an agent run for the prompt (disposition: " + (disposition ?? "none") + ").");
                JObject end = await AwaitAsync(settled.Task, stop.Token, Timeout.InfiniteTimeSpan, null);
                error = Outcome((bool?)end["aborted"] == true);
                state = error == null ? "done" : "failed";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                state = cancel.IsCancellationRequested ? "cancelled" : "limit";
                error = state == "limit"
                    ? "Wall-clock limit of " + options.TimeoutSeconds + " s reached; the agent run was aborted."
                    : "Cancelled by the host; the agent run was aborted.";
                await AbortAsync();
            }
            catch (Exception ex)
            {
                state = "failed";
                error = ex.Message;
                await AbortAsync();
            }
        }
        JObject stats = await SessionStatsAsync();
        bool piExited = await StopAsync();
        if (!piExited) { state = "failed"; error = (error == null ? "" : error + " ") + "The Pi process survived shutdown."; }
        lock (gate)
        {
            foreach (var open in openTools)
            {
                var record = Record("tool_call", open.Value.Step);
                record["tool"] = ShortName(open.Value.Name); record["tool_full"] = open.Value.Name; record["tool_call_id"] = open.Key;
                record["args"] = open.Value.Args; record["duration_ms"] = wall.ElapsedMilliseconds - open.Value.StartedMs;
                record["result_chars"] = null; record["is_error"] = null; record["finished"] = false;
                record["error"] = "The run ended before this tool call finished; its effect on the workbook is unknown.";
                recorder.Trajectory(record);
            }
            if (!string.IsNullOrWhiteSpace(finalText))
            {
                var message = Record("message", step);
                message["role"] = "assistant"; message["text"] = finalText;
                recorder.Trajectory(message);
            }
            recorder.WriteJson("usage.json", Usage(stats));
        }
        if (!recorder.Exists("harness_info.json")) WriteHarnessInfo(prompt, target, keySource, null, null, error);
        int exitCode = ExitCodes.For(state);
        string stopReason;
        lock (gate) stopReason = (string)lastAssistant?["stopReason"];
        recorder.WriteJson("done.json", new JObject
        {
            ["state"] = state, ["final_message"] = finalText, ["error"] = error,
            ["started_at"] = Iso(startedAt), ["ended_at"] = Iso(DateTime.UtcNow), ["elapsed_ms"] = wall.ElapsedMilliseconds,
            ["exit_code"] = exitCode, ["last_stop_reason"] = stopReason, ["pi_process_exited"] = piExited
        });
        Console.WriteLine(new JObject { ["state"] = state, ["error"] = error, ["run_dir"] = recorder.Directory }.ToString(Formatting.None));
        return exitCode;
    }

    private string ReadPrompt()
    {
        string text = File.ReadAllText(Path.GetFullPath(options.PromptFile));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The prompt file is empty.");
        return text;
    }

    private ExcelTarget LoadTarget()
    {
        string context = Path.GetFullPath(options.Context);
        if (!options.Bench)
        {
            // Same guard as PiSmoke --model-smoke; a runner-provided book needs the explicit flag.
            string disposableRoot = Path.GetFullPath(Path.Combine(options.Root, ".local", "smoke")) + Path.DirectorySeparatorChar;
            if (!context.StartsWith(disposableRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Without --bench, BenchHost accepts only contexts under this repository's .local/smoke. Pass --bench for a workbook opened by the benchmark runner.");
        }
        return JsonConvert.DeserializeObject<ExcelTarget>(File.ReadAllText(context))
            ?? throw new InvalidOperationException("The context file is empty.");
    }

    private string EnsureApiKey()
    {
        // Pi resolves "$NAME" in models.json from its own environment. A key set with
        // setx reaches only new shells, so read the user scope explicitly (contract §1)
        // and pass it on to the Pi child through this process. The value is never recorded.
        string name = options.ApiKeyEnv;
        if (name == null) return null;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return "process";
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string value = Environment.GetEnvironmentVariable(name, scope);
            if (string.IsNullOrEmpty(value)) continue;
            Environment.SetEnvironmentVariable(name, value);
            return scope == EnvironmentVariableTarget.User ? "user" : "machine";
        }
        throw new InvalidOperationException("Environment variable " + name + " is not set. Set it yourself (setx " + name + " <key>); BenchHost never stores or prints keys.");
    }

    private void CheckSelectedModel(JObject piState)
    {
        var model = piState?["model"] as JObject
            ?? throw new InvalidOperationException("Pi started without a model. Check the provider credentials and models.json.");
        lock (gate)
        {
            selectedModel = model;
            costKnown = (model["cost"] as JObject)?.Properties().Any(p => p.Value.Type == JTokenType.Float || p.Value.Type == JTokenType.Integer ? (double)p.Value > 0 : false) == true;
        }
        if (options.Model == null) return;
        // Pi also accepts fuzzy --model matches; a benchmark run must use exactly the requested model.
        string id = (string)model["id"], provider = (string)model["provider"];
        bool sameModel = id == options.Model || provider + "/" + id == options.Model;
        if (!sameModel || (options.Provider != null && provider != options.Provider))
            throw new InvalidOperationException("Pi selected " + provider + "/" + id + " instead of the requested " + (options.Provider ?? "?") + "/" + options.Model + ".");
    }

    private static void CheckTools(JObject catalog)
    {
        string[] active = catalog["activeTools"]?.Values<string>().ToArray() ?? new string[0];
        string[] excel = catalog["excelTools"]?.Values<string>().ToArray() ?? new string[0];
        if (excel.Length == 0 || active.Length == 0) throw new InvalidOperationException("No live Excel MCP tools loaded; see pi_events.jsonl and pi_stderr.log.");
        if (!active.All(t => t.StartsWith(ExcelPrefix, StringComparison.Ordinal))) throw new InvalidOperationException("Unexpected non-Excel tool was enabled: " + string.Join(", ", active));
    }

    private void OnEvent(object sender, PiEvent e)
    {
        recorder.PiEvent(e.Json);
        lock (gate)
        {
            switch (e.Type)
            {
                case "extension_ui_request":
                    if ((string)e.Json["method"] != "notify") break;
                    string text = (string)e.Json["message"] ?? "";
                    if (text.StartsWith("APERTURE_STATUS ", StringComparison.Ordinal)) status.TrySetResult(JObject.Parse(text.Substring(16)));
                    else if (text.StartsWith("APERTURE_PROVIDER_USAGE ", StringComparison.Ordinal))
                    {
                        // Streamed before message_end; correlated with it by the response id.
                        var raw = JObject.Parse(text.Substring(24));
                        providerUsage[(string)raw["id"] ?? ""] = raw;
                    }
                    else if (notices.Count < 50) notices.Add(text);
                    break;
                case "turn_start":
                    if (promptSent) step++;
                    break;
                case "message_start":
                    if ((string)e.Json["message"]?["role"] == "assistant") messageStartedMs = wall.ElapsedMilliseconds;
                    break;
                case "message_end":
                    if ((string)e.Json["message"]?["role"] == "assistant") OnAssistantMessage((JObject)e.Json["message"]);
                    break;
                case "tool_execution_start":
                    openTools[(string)e.Json["toolCallId"] ?? ""] = new ToolStart
                    { Name = (string)e.Json["toolName"] ?? "", Args = e.Json["args"]?.DeepClone(), Step = step, StartedMs = wall.ElapsedMilliseconds };
                    break;
                case "tool_execution_end":
                    OnToolEnd(e.Json);
                    break;
                case "compaction_end":
                    var compaction = Record("context_compaction", step);
                    compaction["reason"] = e.Json["reason"];
                    compaction["tokens_before"] = e.Json["result"]?["tokensBefore"];
                    compaction["tokens_after"] = e.Json["result"]?["estimatedTokensAfter"];
                    compaction["aborted"] = e.Json["aborted"];
                    compaction["error"] = e.Json["errorMessage"];
                    recorder.Trajectory(compaction);
                    break;
                case "auto_retry_start":
                    var retry = Record("retry", step);
                    retry["attempt"] = e.Json["attempt"]; retry["max_attempts"] = e.Json["maxAttempts"];
                    retry["delay_ms"] = e.Json["delayMs"]; retry["error"] = e.Json["errorMessage"];
                    recorder.Trajectory(retry);
                    break;
                case "agent_settled":
                    if (promptSent) settled.TrySetResult(e.Json);
                    break;
                case "process_exit":
                    settled.TrySetException(new IOException("Pi exited before the agent run settled; see pi_stderr.log."));
                    status.TrySetException(new IOException("Pi exited during startup; see pi_stderr.log."));
                    break;
            }
        }
    }

    private void OnAssistantMessage(JObject message)
    {
        var usage = message["usage"] as JObject;
        JObject normalized = NormalizeUsage(usage);
        long? latency = (long?)message["durationMs"] ?? (messageStartedMs >= 0 ? wall.ElapsedMilliseconds - messageStartedMs : (long?)null);
        string responseId = (string)message["responseId"] ?? "";
        providerUsage.TryGetValue(responseId, out JObject raw);
        providerUsage.Remove(responseId);
        // Pi sets responseModel only when the provider reported a model different from the request.
        string returned = (string)raw?["model"] ?? (string)message["responseModel"] ?? (string)message["model"];
        string stopReason = (string)message["stopReason"];
        JToken billed = raw?["usage"]?["cost"];
        double? providerCost = billed != null && (billed.Type == JTokenType.Float || billed.Type == JTokenType.Integer) ? (double)billed : (double?)null;
        var record = Record("llm_call", step);
        record["pi_provider"] = message["provider"]; record["api"] = message["api"];
        record["provider"] = raw?["provider"]; // upstream reported by an aggregator, if any
        record["model_requested"] = message["model"]; record["model_returned"] = returned;
        record["usage"] = normalized;
        record["cost"] = providerCost;
        record["cost_pi_estimate"] = costKnown && normalized != null ? usage["cost"]?["total"] : null;
        record["provider_usage"] = raw?["usage"];
        record["latency_ms"] = latency;
        record["http_status"] = null; // not exposed by Pi RPC
        record["generation_id"] = message["responseId"];
        record["finish_reason"] = message["rawStopReason"]; record["stop_reason"] = stopReason;
        record["thinking_level"] = message["thinkingLevel"];
        record["error"] = message["errorMessage"];
        recorder.Trajectory(record);

        llmCalls++;
        if (providerCost != null) { providerCostCalls++; providerCostTotal += providerCost.Value; }
        if (stopReason == "error" || !string.IsNullOrEmpty((string)message["errorMessage"])) llmErrors++;
        if (latency != null) llmTimeMs += latency.Value;
        if (returned != null) { modelsReturned.TryGetValue(returned, out int seen); modelsReturned[returned] = seen + 1; }
        if (normalized != null)
        {
            llmCallsWithUsage++;
            tokPrompt += (long)normalized["prompt"]; tokCompletion += (long)normalized["completion"];
            tokCached += (long)normalized["cached"]; tokCacheWrite += (long)normalized["cache_write"];
            tokReasoning += (long?)normalized["reasoning"] ?? 0; tokTotal += (long)normalized["total"];
            var cost = usage["cost"];
            costInput += (double?)cost?["input"] ?? 0; costOutput += (double?)cost?["output"] ?? 0;
            costCacheRead += (double?)cost?["cacheRead"] ?? 0; costCacheWrite += (double?)cost?["cacheWrite"] ?? 0;
            costTotal += (double?)cost?["total"] ?? 0;
        }
        lastAssistant = message;
        string text = string.Concat((message["content"] as JArray ?? new JArray()).Where(p => (string)p["type"] == "text").Select(p => (string)p["text"]));
        if (!string.IsNullOrWhiteSpace(text)) finalText = text;
        messageStartedMs = -1;
    }

    private static JObject NormalizeUsage(JObject usage)
    {
        if (usage == null) return null;
        long input = (long?)usage["input"] ?? 0, output = (long?)usage["output"] ?? 0;
        long read = (long?)usage["cacheRead"] ?? 0, write = (long?)usage["cacheWrite"] ?? 0;
        // Pi keeps zeros when the provider sent no usage; the contract wants null then.
        if (input + output + read + write == 0) return null;
        return new JObject
        {
            ["prompt"] = input + read + write, ["completion"] = output, ["cached"] = read, ["cache_write"] = write,
            ["reasoning"] = usage["reasoning"], ["total"] = (long?)usage["totalTokens"] ?? input + output + read + write
        };
    }

    private void OnToolEnd(JObject json)
    {
        string id = (string)json["toolCallId"] ?? "";
        openTools.TryGetValue(id, out ToolStart start);
        openTools.Remove(id);
        string name = (string)json["toolName"] ?? start?.Name ?? "";
        bool isError = (bool?)json["isError"] == true;
        string text = ResultText(json["result"]);
        long duration = (long?)json["durationMs"] ?? (start == null ? 0 : wall.ElapsedMilliseconds - start.StartedMs);
        var record = Record("tool_call", start?.Step ?? step);
        record["tool"] = ShortName(name); record["tool_full"] = name; record["tool_call_id"] = id;
        record["args"] = start?.Args; record["duration_ms"] = duration;
        record["result_chars"] = text.Length; record["is_error"] = isError;
        record["error"] = isError ? (text.Length > 2000 ? text.Substring(0, 2000) : text) : null;
        recorder.Trajectory(record);
        toolCalls++;
        if (isError) toolErrors++;
        toolTimeMs += duration;
    }

    private static string ResultText(JToken result)
    {
        var blocks = result?["content"] as JArray ?? result?["structuredContent"]?["content"] as JArray;
        return blocks == null ? "" : string.Concat(blocks.Where(b => (string)b["type"] == "text").Select(b => (string)b["text"]));
    }

    private static string ShortName(string name) => name.StartsWith(ExcelPrefix, StringComparison.Ordinal) ? name.Substring(ExcelPrefix.Length) : name;

    private string Outcome(bool aborted)
    {
        lock (gate)
        {
            if (aborted) return "Pi reported an aborted run.";
            if (lastAssistant == null) return "The agent run produced no assistant response.";
            string reason = (string)lastAssistant["stopReason"];
            if (reason == "error") return "Provider/assistant error: " + ((string)lastAssistant["errorMessage"] ?? "unknown");
            if (reason == "aborted") return "The last assistant response was aborted.";
            return null;
        }
    }

    private JObject Usage(JObject stats)
    {
        bool anyUsage = llmCallsWithUsage > 0;
        JToken piCost = costKnown && anyUsage ? (JToken)Math.Round(costTotal, 8) : null;
        return new JObject
        {
            ["llm_calls"] = llmCalls, ["llm_calls_with_usage"] = llmCallsWithUsage, ["llm_errors"] = llmErrors,
            ["tokens"] = anyUsage ? new JObject
            {
                ["prompt"] = tokPrompt, ["completion"] = tokCompletion, ["cached"] = tokCached,
                ["cache_write"] = tokCacheWrite, ["reasoning"] = tokReasoning, ["total"] = tokTotal
            } : null,
            // Billed cost reported by the provider (usage.cost), summed over completed responses;
            // an aborted stream has no usage block. Without any, fall back to Pi's estimate.
            ["cost"] = providerCostCalls > 0 ? (JToken)Math.Round(providerCostTotal, 8) : piCost,
            ["cost_source"] = providerCostCalls > 0 ? (providerCostCalls >= llmCallsWithUsage ? "provider" : "provider_partial") : piCost != null ? "pi_model_rates" : null,
            ["cost_calls_reported"] = providerCostCalls,
            ["cost_currency"] = options.CostCurrency,
            // Pi multiplies tokens by the selected model's per-million rates (models.json or its
            // catalog); an aggregator may route to a pricier upstream than those rates.
            ["cost_pi_estimate"] = piCost,
            ["cost_pi_breakdown"] = piCost == null ? null : new JObject
            {
                ["input"] = Math.Round(costInput, 8), ["output"] = Math.Round(costOutput, 8),
                ["cache_read"] = Math.Round(costCacheRead, 8), ["cache_write"] = Math.Round(costCacheWrite, 8)
            },
            ["cost_rates_per_million"] = selectedModel?["cost"]?.DeepClone(),
            ["llm_time_ms"] = llmTimeMs, ["tool_calls"] = toolCalls, ["tool_errors"] = toolErrors, ["tool_time_ms"] = toolTimeMs,
            ["wall_time_ms"] = wall.ElapsedMilliseconds,
            ["models_returned"] = JObject.FromObject(modelsReturned),
            ["pi_session_stats"] = stats
        };
    }

    private void WriteHarnessInfo(string prompt, ExcelTarget target, string keySource, JObject piState, JObject catalog, string error)
    {
        JObject systemPrompt = null, mcp = null;
        if (session != null)
        {
            string path = Path.Combine(session.SessionDirectory, "aperture-system.md");
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                // The last line binds this session's workbook IDs, so it differs on every run.
                int binding = text.IndexOf("\nSession binding: ", StringComparison.Ordinal);
                systemPrompt = new JObject
                {
                    ["path"] = path, ["chars"] = text.Length, ["sha256"] = Sha256(text),
                    ["template_sha256"] = Sha256(binding < 0 ? text : text.Substring(0, binding))
                };
            }
            string mcpPath = Path.Combine(session.SessionDirectory, ".pi", "mcp.json");
            if (File.Exists(mcpPath)) mcp = JObject.Parse(File.ReadAllText(mcpPath));
        }
        var definitions = catalog?["excelToolDefinitions"] as JArray;
        var tools = new JArray();
        if (definitions != null)
            foreach (JObject tool in definitions.OfType<JObject>())
                tools.Add(new JObject
                {
                    ["name"] = ShortName((string)tool["name"] ?? ""), ["full_name"] = tool["name"],
                    ["description"] = tool["description"], ["parameters"] = tool["parameters"],
                    ["sha256"] = Sha256(tool.ToString(Formatting.None))
                });
        else if (catalog?["excelTools"] is JArray names)
            foreach (string name in names.Values<string>()) tools.Add(new JObject { ["name"] = ShortName(name), ["full_name"] = name });
        var model = piState?["model"] as JObject;
        string[] noticesCopy;
        lock (gate) noticesCopy = notices.ToArray();
        recorder.WriteJson("harness_info.json", new JObject
        {
            ["harness"] = "aperture2", ["contract_version"] = 1,
            ["harness_version"] = typeof(PiSession).Assembly.GetName().Version.ToString(),
            ["host"] = "tools/BenchHost",
            ["git"] = new JObject
            {
                ["aperture2"] = Git(options.Root), ["external/pi"] = Git(Path.Combine(options.Root, "external", "pi")),
                ["external/thepexcel"] = Git(Path.Combine(options.Root, "external", "thepexcel"))
            },
            ["build"] = new JObject
            {
                ["bench_host_sha256"] = FileSha256(typeof(BenchRun).Assembly.Location),
                ["runtime_sha256"] = FileSha256(typeof(PiSession).Assembly.Location),
                ["node"] = Run("node", "--version", options.Root)
            },
            ["provider"] = options.Provider, ["model"] = options.Model,
            ["model_selected"] = model == null ? null : new JObject
            {
                ["provider"] = model["provider"], ["id"] = model["id"], ["api"] = model["api"], ["baseUrl"] = model["baseUrl"],
                ["reasoning"] = model["reasoning"], ["contextWindow"] = model["contextWindow"], ["maxTokens"] = model["maxTokens"],
                ["cost_per_million"] = model["cost"], ["compat"] = model["compat"]
            },
            ["thinking_requested"] = options.Thinking, ["thinking_level"] = piState?["thinkingLevel"],
            ["system_prompt"] = systemPrompt,
            ["tools"] = tools, ["tools_sha256"] = tools.Count == 0 ? null : Sha256(tools.ToString(Formatting.None)),
            ["active_tools"] = catalog?["activeTools"],
            ["runtime_settings"] = new JObject
            {
                ["timeout_s"] = options.TimeoutSeconds, ["bench_mode"] = options.Bench, ["cost_currency"] = options.CostCurrency,
                ["api_key_env"] = options.ApiKeyEnv == null ? null : new JObject { ["name"] = options.ApiKeyEnv, ["source"] = keySource },
                ["pi_session_dir"] = session?.SessionDirectory, ["pi_session_id"] = piState?["sessionId"], ["mcp"] = mcp
            },
            ["target"] = target == null ? null : new JObject
            {
                ["excelProcessId"] = target.ExcelProcessId, ["workbookName"] = target.WorkbookName, ["workbookFullName"] = target.WorkbookFullName
            },
            ["prompt"] = prompt == null ? null : new JObject { ["file"] = Path.GetFullPath(options.PromptFile), ["chars"] = prompt.Length, ["sha256"] = Sha256(prompt) },
            ["notices"] = new JArray(noticesCopy),
            ["started_at"] = Iso(startedAt),
            ["error"] = error
        });
    }

    private async Task AbortAsync()
    {
        if (session == null || !session.IsRunning) return;
        try
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await session.AbortAsync(deadline.Token);
        }
        catch (Exception ex) { recorder.Diagnostic("BenchHost: abort was not confirmed: " + ex.Message); }
        // agent_settled normally arrives before the abort response; allow a short grace.
        await Task.WhenAny(settled.Task, Task.Delay(2000));
    }

    private async Task<JObject> SessionStatsAsync()
    {
        if (session == null || !session.IsRunning) return null;
        try
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                return (JObject)(await session.SendCommandAsync(new JObject { ["type"] = "get_session_stats" }, deadline.Token))["data"];
        }
        catch (Exception ex) { recorder.Diagnostic("BenchHost: session stats unavailable: " + ex.Message); return null; }
    }

    private async Task<bool> StopAsync()
    {
        if (session == null) return true;
        int pid = session.ProcessId;
        try { await session.StopAsync(); }
        catch (Exception ex) { recorder.Diagnostic("BenchHost: stop failed: " + ex.Message); session.Dispose(); }
        // StopAsync closes stdin, waits briefly, then the job object kills Pi and its MCP child.
        for (int i = 0; i < 50 && ProcessExists(pid); i++) await Task.Delay(100);
        return !ProcessExists(pid);
    }

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken token, TimeSpan timeout, string timeoutMessage)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            Task winner = await Task.WhenAny(task, cancelled.Task, Task.Delay(timeout));
            if (winner == cancelled.Task) token.ThrowIfCancellationRequested();
            if (winner != task) throw new TimeoutException(timeoutMessage);
        }
        return await task;
    }

    private JObject Record(string type, int recordStep) => new JObject { ["ts"] = Iso(DateTime.UtcNow), ["step"] = recordStep, ["type"] = type };

    private static string Iso(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Sha256(string text)
    {
        using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)));
    }

    private static string FileSha256(string path)
    {
        try { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Hex(sha.ComputeHash(stream)); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return null; }
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    private static JObject Git(string directory)
    {
        string sha = Run("git", "rev-parse HEAD", directory);
        if (sha == null) return null;
        string changes = Run("git", "status --porcelain --untracked-files=no", directory);
        return new JObject { ["sha"] = sha, ["dirty"] = changes == null ? null : (JToken)(changes.Length > 0) };
    }

    private static string Run(string file, string arguments, string directory)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false)
            };
            using (var process = Process.Start(info))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> ignored = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
                return process.ExitCode == 0 ? output.Result.Trim() : null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException) { return null; }
    }

    private static bool ProcessExists(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        session?.Dispose();
        cancel.Dispose();
    }
}
