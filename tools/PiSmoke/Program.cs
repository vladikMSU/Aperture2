using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        string context = Value(args, "--context");
        if (!protocolOnly && context == null) throw new ArgumentException("Use --context <target.json> or --protocol-only.");
        ExcelTarget target = context == null ? null : JsonConvert.DeserializeObject<ExcelTarget>(File.ReadAllText(context));
        var options = new PiSessionOptions { RepositoryRoot = root, EnableExcelTools = !protocolOnly };
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
            int pid = session.ProcessId;
            await session.StopAsync();
            Require(!ProcessExists(pid), "Pi process survived shutdown.");
            Console.WriteLine(new JObject
            {
                ["ok"] = true, ["protocolOnly"] = protocolOnly, ["rpcGetState"] = true,
                ["sessionId"] = state["data"]?["sessionId"], ["modelSelected"] = state["data"]?["model"] != null,
                ["activeTools"] = new JArray(active), ["excelTools"] = new JArray(excel), ["notices"] = new JArray(notices),
                ["cleanShutdown"] = true, ["modelRequestMade"] = false
            }.ToString(Formatting.Indented));
        }
        return 0;
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
