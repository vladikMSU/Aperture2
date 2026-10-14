using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Aperture.Excel;
using Aperture.Runtime;
using Newtonsoft.Json.Linq;

internal static class PaneLifecycleHarness
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    private static T Field<T>(object instance, string name)
    {
        return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

    private static void Set(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }

    private static Task Call(object instance, string method)
    {
        var target = instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        return (Task)target.Invoke(instance, method == "SendAsync" ? new object[] { "audit" } : null);
    }

    private static ChatPaneControl CreatePane()
    {
        var pane = new ChatPaneControl(() => new WorkbookBinding(), () => true, () => { });
        CreateHandles(pane); // Every child must have a real handle for cross-thread checks.
        return pane;
    }

    private static ChatPaneView View(ChatPaneControl pane) { return Field<ChatPaneView>(pane, "view"); }

    private static void CreateHandles(Control control)
    {
        var handle = control.Handle;
        foreach (Control child in control.Controls) CreateHandles(child);
    }

    private static Task CallWithoutContext(object instance, string method)
    {
        // Excel's VSTO host can have a UI thread and a valid HWND without a .NET
        // SynchronizationContext. Only the test's continuation gets its context back.
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Check(SynchronizationContext.Current == null, "The null-context regression was not configured.");
            return Call(instance, method);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static Task FlushUi(Control pane)
    {
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.BeginInvoke(new Action(() => complete.SetResult(true)));
        return complete.Task;
    }

    private static void ResetFake()
    {
        PiSession.Starts = 0;
        PiSession.Prompts = 0;
        PiSession.LastPromptThreadId = 0;
        PiSession.StartGate = new TaskCompletionSource<PiSession>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task VerifyAsync()
    {
        ResetFake();
        using (var pane = CreatePane())
        {
            var send = Call(pane, "SendAsync");
            Check(PiSession.Starts == 1, "Startup was not reached.");
            await Call(pane, "AbortAsync");
            var late = new PiSession();
            PiSession.StartGate.SetResult(late);
            await send;
            Check(late.Disposed, "Cancelled startup process leaked.");
            Check(PiSession.Prompts == 0, "Stop during startup sent a prompt.");
            Check(!Field<bool>(pane, "busy"), "Cancelled startup remained busy.");
        }

        foreach (var method in new[] { "RebindAsync", "ResetChatAsync" })
        {
            ResetFake();
            using (var pane = CreatePane())
            {
                var old = new PiSession { StopGate = new TaskCompletionSource<bool>() };
                Set(pane, "session", old);
                var transition = Call(pane, method);
                Check(Field<bool>(pane, "busy"), method + " did not gate the transition.");
                await Call(pane, "SendAsync");
                Check(PiSession.Starts == 0, method + " allowed a concurrent startup.");
                Check(PiSession.Prompts == 0, method + " allowed a concurrent prompt.");
                old.StopGate.SetResult(true);
                await transition;
                Check(old.Disposed, method + " leaked the previous session.");
                Check(!Field<bool>(pane, "busy"), method + " remained busy.");
            }
        }

        ResetFake();
        var closing = CreatePane();
        var pending = Call(closing, "SendAsync");
        closing.Dispose();
        var returned = new PiSession();
        PiSession.StartGate.SetResult(returned);
        await pending;
        Check(returned.Disposed, "Disposed pane leaked a late startup process.");
        Check(PiSession.Prompts == 0, "Disposed pane sent a prompt.");
        Console.WriteLine("PASS: " + assertions + " existing pane lifecycle assertions.");

        await VerifyWithoutSynchronizationContextAsync();
        await VerifyActivityAsync();
        await VerifyPromptDispositionAsync();
        await VerifyToolCallFormationAsync();
        Console.WriteLine("PASS: " + assertions + " total pane lifecycle/thread assertions (actual controls and handles; fake Pi/COM; no Excel/model).");
    }

    private static async Task VerifyWithoutSynchronizationContextAsync()
    {
        Console.WriteLine("CASE: null-context startup success, completion from worker thread.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Starts == 1, "Null-context Send did not reach startup.");
            var running = new PiSession();
            await Task.Run(() => PiSession.StartGate.SetResult(running));
            await send;
            Check(PiSession.Prompts == 1, "Null-context startup did not submit exactly one prompt.");
            Check(PiSession.LastPromptThreadId == Thread.CurrentThread.ManagedThreadId,
                "Prompt write admission must occur in the owning UI action before a subsequent Stop.");
            Check(View(pane).Transcript.Exists(text => text.Contains("audit")), "User message was not rendered.");
            Check(View(pane).PromptCleared, "Prompt was not cleared on the UI thread.");
            Check(Field<bool>(pane, "busy"), "Prompt acceptance incorrectly marked the run complete.");

            Console.WriteLine("CASE: RPC events and diagnostics emitted on worker threads.");
            await Task.Run(() =>
            {
                running.EmitEvent(JObject.Parse("{type:'message_update',assistantMessageEvent:{type:'text_delta',delta:'worker response'}}"));
                running.EmitDiagnostic("worker diagnostic");
            });
            await FlushUi(pane);
            Check(View(pane).Transcript.Exists(text => text.Contains("worker response")), "Background RPC text was not rendered.");
            Check(View(pane).StatusText == "worker diagnostic", "Background RPC diagnostic was not rendered.");
            await Task.Run(() => running.EmitEvent(JObject.Parse("{type:'agent_settled'}")));
            await FlushUi(pane);
            Check(!Field<bool>(pane, "busy"), "Background settled event did not release busy state.");
        }

        Console.WriteLine("CASE: null-context startup failure from worker thread.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var send = CallWithoutContext(pane, "SendAsync");
            await Task.Run(() => PiSession.StartGate.SetException(new InvalidOperationException("test startup failure")));
            await send;
            Check(View(pane).Transcript.Exists(text => text.Contains("test startup failure")), "Startup failure was not shown.");
            Check(!Field<bool>(pane, "busy"), "Startup failure remained busy.");
            Check(PiSession.Prompts == 0, "Failed startup sent a prompt.");
        }

        Console.WriteLine("CASE: null-context startup cancellation from worker thread.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var send = CallWithoutContext(pane, "SendAsync");
            await Task.Run(() => PiSession.StartGate.SetCanceled());
            await send;
            Check(!Field<bool>(pane, "busy"), "Cancelled startup remained busy without a context.");
            Check(PiSession.Prompts == 0, "Cancelled startup sent a prompt without a context.");
        }

        Console.WriteLine("CASE: null-context stop during startup and late worker completion.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var send = CallWithoutContext(pane, "SendAsync");
            await CallWithoutContext(pane, "AbortAsync");
            var late = new PiSession();
            await Task.Run(() => PiSession.StartGate.SetResult(late));
            await send;
            Check(late.Disposed, "Cancelled late startup process leaked without a context.");
            Check(PiSession.Prompts == 0, "Stopped startup submitted a prompt without a context.");
            Check(!Field<bool>(pane, "busy"), "Stopped startup remained busy without a context.");
        }

        foreach (var method in new[] { "RebindAsync", "ResetChatAsync" })
        {
            Console.WriteLine("CASE: null-context " + method + " with asynchronous stop.");
            ResetFake();
            using (var pane = CreatePane())
            {
                var old = new PiSession { StopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
                Set(pane, "session", old);
                var transition = CallWithoutContext(pane, method);
                await Task.Run(() => old.StopGate.SetResult(true));
                await transition;
                Check(old.Disposed, method + " leaked its old session without a context.");
                Check(!Field<bool>(pane, "busy"), method + " did not return the pane to idle without a context.");
                Check(!String.IsNullOrEmpty(pane.BoundWorkbookId), method + " lost the book binding without a context.");
            }
        }

        Console.WriteLine("CASE: null-context prompt response failure after startup has already completed.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = new PiSession
            {
                PromptGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            Set(pane, "session", running);
            var send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Prompts == 1, "Existing session did not receive the prompt.");
            await Task.Run(() => running.PromptGate.SetException(new InvalidOperationException("test prompt rejection")));
            await send;
            Check(View(pane).Transcript.Exists(text => text.Contains("test prompt rejection")), "Prompt response failure was not shown.");
            Check(!Field<bool>(pane, "busy"), "Prompt response failure left the pane busy.");
        }

        Console.WriteLine("CASE: null-context prompt timeout keeps ownership until asynchronous abort confirms idle.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = new PiSession
            {
                PromptGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                AbortGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            Set(pane, "session", running);
            var send = CallWithoutContext(pane, "SendAsync");
            await Task.Run(() => running.PromptGate.SetException(new TimeoutException("test prompt timeout")));
            await send;
            Check(Field<bool>(pane, "busy"), "An uncertain prompt timeout released the turn.");
            Check(View(pane).Busy && View(pane).CanStop, "Uncertain prompt timeout did not keep Stop enabled.");
            var previousEnds = View(pane).AssistantEndCount;
            var abort = CallWithoutContext(pane, "AbortAsync");
            Check(!View(pane).CanStop, "Duplicate Stop was not disabled while abort was pending.");
            await Task.Run(() => running.AbortGate.SetResult(true));
            await abort;
            Check(!Field<bool>(pane, "busy"), "Confirmed abort did not release the turn without a context.");
            Check(View(pane).AssistantEndCount > previousEnds, "Confirmed abort kept the prior assistant stream open.");
            Check(View(pane).StatusText.Contains("остановлен"), "Confirmed abort status was not rendered on the UI thread.");
        }

        Console.WriteLine("CASE: null-context asynchronous abort failure preserves the active turn.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = new PiSession
            {
                AbortGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            Set(pane, "session", running);
            await CallWithoutContext(pane, "SendAsync");
            var abort = CallWithoutContext(pane, "AbortAsync");
            await Task.Run(() => running.AbortGate.SetException(new InvalidOperationException("test abort failure")));
            await abort;
            Check(Field<bool>(pane, "busy"), "Failed abort released a possibly active turn.");
            Check(View(pane).CanStop, "Failed abort left Stop permanently disabled.");
            Check(View(pane).Transcript.Exists(text => text.Contains("test abort failure")), "Abort failure was not rendered on the UI thread.");
        }
    }

    private static async Task<PiSession> StartSessionAsync(ChatPaneControl pane)
    {
        var send = CallWithoutContext(pane, "SendAsync");
        var running = new PiSession();
        await Task.Run(() => PiSession.StartGate.SetResult(running));
        await send;
        return running;
    }

    private static async Task EmitAsync(ChatPaneControl pane, PiSession session, params string[] events)
    {
        await Task.Run(() =>
        {
            foreach (var value in events) session.EmitEvent(JObject.Parse(value));
        });
        await FlushUi(pane);
    }

    private static bool HasTerminal(ChatPaneView view, string status)
    {
        return view.Activity.Exists(item => (string)item["type"] == "aperture_run_end" && (string)item["status"] == status);
    }

    private static async Task VerifyActivityAsync()
    {
        Console.WriteLine("CASE: background thinking, fallback and parallel tool activity preserve identities and progress.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = await StartSessionAsync(pane);
            var view = View(pane);
            await EmitAsync(pane, running,
                "{type:'agent_start'}", "{type:'turn_start'}",
                "{type:'message_start',message:{role:'assistant',content:[]}}",
                "{type:'message_update',assistantMessageEvent:{type:'thinking_start',contentIndex:0}}",
                "{type:'message_update',assistantMessageEvent:{type:'thinking_delta',contentIndex:0,delta:'reasoning one'}}",
                "{type:'message_update',assistantMessageEvent:{type:'thinking_end',contentIndex:0,content:'reasoning one complete'}}",
                "{type:'message_update',assistantMessageEvent:{type:'text_delta',contentIndex:1,delta:'answer'}}",
                "{type:'message_end',message:{role:'assistant',stopReason:'toolUse',content:[{type:'thinking',thinking:'reasoning one complete'}]}}",
                "{type:'tool_execution_start',toolCallId:'call-one',toolName:'excel_target',args:{}}",
                "{type:'tool_execution_start',toolCallId:'call-two',toolName:'excel_read_range',args:{range:'A1:B2'}}",
                "{type:'tool_execution_update',toolCallId:'call-two',toolName:'excel_read_range',args:{range:'A1:B2'},partialResult:{content:[{type:'text',text:'row one'}]}}",
                "{type:'tool_execution_end',toolCallId:'call-two',toolName:'excel_read_range',result:{content:[{type:'text',text:'range result'}]},isError:false,durationMs:7}",
                "{type:'tool_execution_end',toolCallId:'call-one',toolName:'excel_target',result:{content:[{type:'text',text:'target result'}]},isError:false}",
                "{type:'turn_end',message:{role:'assistant',stopReason:'toolUse'},toolResults:[]}",
                "{type:'turn_start'}",
                "{type:'message_start',message:{role:'assistant',content:[]}}",
                "{type:'message_update',assistantMessageEvent:{type:'text_delta',contentIndex:1,delta:'final'}}",
                "{type:'message_end',message:{role:'assistant',stopReason:'stop',content:[{type:'thinking',thinking:'fallback reasoning two'},{type:'text',text:'final answer'}]}}",
                "{type:'agent_end',messages:[],willRetry:false}");
            Check(view.Activity.FindAll(item => (string)item["type"] == "message_update").Count == 3,
                "Activity must include every thinking event but exclude assistant text deltas.");
            Check(view.Activity.Exists(item => (string)item["type"] == "message_end" &&
                (string)item["message"]?["content"]?[0]?["thinking"] == "fallback reasoning two"),
                "Authoritative message_end reasoning fallback was lost.");
            Check(view.Activity.FindAll(item => (string)item["type"] == "tool_execution_start").Count == 2,
                "Multiple tool starts were not delivered separately.");
            var progress = view.Activity.Find(item => (string)item["type"] == "tool_execution_update");
            Check((string)progress?["toolCallId"] == "call-two" &&
                (string)progress?["partialResult"]?["content"]?[0]?["text"] == "row one",
                "Tool progress lost its identity or partial result.");
            var ends = view.Activity.FindAll(item => (string)item["type"] == "tool_execution_end");
            Check((string)ends[0]["toolCallId"] == "call-two" && (string)ends[1]["toolCallId"] == "call-one",
                "Out-of-order tool completion was reordered or relabelled.");
            Check((string)ends[0]["result"]?["content"]?[0]?["text"] == "range result" && (int?)ends[0]["durationMs"] == 7,
                "Tool result payload was not forwarded intact.");
            Check(!view.Transcript.Exists(text => text.StartsWith("Инструмент:")), "Tool activity duplicated legacy transcript lines.");
            Check(view.LastAssistantReplacement == "final answer", "Authoritative final text did not reconcile an incomplete delta stream.");
            Check(Field<bool>(pane, "busy"), "agent_end incorrectly settled the run before Pi's terminal event.");
            await EmitAsync(pane, running, "{type:'agent_settled',aborted:false}");
            Check(!Field<bool>(pane, "busy") && view.StatusText == "Готово" && !view.StatusIsError,
                "A successful settled run did not return to idle.");

            var stale = running.CaptureEventDelivery(JObject.Parse("{type:'tool_execution_start',toolCallId:'stale',toolName:'wrong-session'}"));
            var completedActivityCount = view.Activity.Count;
            Field<WorkbookBinding>(pane, "binding").ValidationFailure = "preflight binding failed";
            await CallWithoutContext(pane, "SendAsync");
            Check(view.Activity.Count == completedActivityCount,
                "A preflight error rewrote the previous completed activity.");
            await CallWithoutContext(pane, "RebindAsync");
            Check(view.Activity.Count == completedActivityCount,
                "An idle rebind marked the previous successful run as stopped.");

            await CallWithoutContext(pane, "ResetChatAsync");
            Check(view.Activity.Count == 0, "New chat did not clear old activity.");
            await Task.Run(stale);
            await FlushUi(pane);
            Check(view.Activity.Count == 0 && !view.Transcript.Exists(text => text.Contains("wrong-session")),
                "An already queued event from the old Pi session contaminated the new chat.");
        }

        Console.WriteLine("CASE: provider failure remains an error at settlement; successful retry clears that outcome.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = await StartSessionAsync(pane);
            var view = View(pane);
            await EmitAsync(pane, running,
                "{type:'message_end',message:{role:'assistant',content:[],stopReason:'error',errorMessage:'provider unavailable'}}",
                "{type:'agent_settled',aborted:false}");
            Check(view.StatusIsError && view.StatusText != "Готово", "Provider error was overwritten by a successful status.");
            Check(HasTerminal(view, "error") && !Field<bool>(pane, "busy"), "Failed provider run left activity running.");
            await CallWithoutContext(pane, "SendAsync");
            await EmitAsync(pane, running,
                "{type:'agent_start'}",
                "{type:'message_end',message:{role:'assistant',content:[],stopReason:'error',errorMessage:'temporary failure'}}",
                "{type:'agent_end',messages:[],willRetry:true}",
                "{type:'auto_retry_start',attempt:1,maxAttempts:3,delayMs:100,errorMessage:'temporary failure'}");
            Check(Field<bool>(pane, "busy"), "Automatic retry released the active run.");
            Check(view.Activity.Exists(item => (string)item["type"] == "auto_retry_start"), "Automatic retry activity was dropped.");
            await EmitAsync(pane, running,
                "{type:'message_end',message:{role:'assistant',content:[{type:'text',text:'recovered'}],stopReason:'stop'}}",
                "{type:'auto_retry_end',success:true,attempt:1}",
                "{type:'agent_settled',aborted:false}");
            Check(view.StatusText == "Готово" && !view.StatusIsError && !Field<bool>(pane, "busy"),
                "A successful Pi retry retained the earlier provider failure.");
        }

        Console.WriteLine("CASE: confirmed Stop and process errors terminate active cards; stale sessions remain isolated.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = await StartSessionAsync(pane);
            var view = View(pane);
            await EmitAsync(pane, running,
                "{type:'tool_execution_start',toolCallId:'pending',toolName:'excel_target',args:{}}");
            await CallWithoutContext(pane, "AbortAsync");
            Check(HasTerminal(view, "stopped") && !Field<bool>(pane, "busy"), "Confirmed Stop left activity running.");
            await EmitAsync(pane, running, "{type:'agent_settled',aborted:true}");
            Check(view.StatusText.Contains("остановлен") && !view.StatusIsError, "Aborted settlement became success or failure.");
            await CallWithoutContext(pane, "SendAsync");
            await EmitAsync(pane, running,
                "{type:'tool_execution_start',toolCallId:'interrupted',toolName:'excel_target',args:{}}",
                "{type:'process_exit'}");
            Check(HasTerminal(view, "error") && view.StatusIsError && !Field<bool>(pane, "busy"), "Process exit left activity running.");
            Check(view.Activity.Exists(item => (string)item["type"] == "process_exit"), "Process-exit lifecycle event was not forwarded.");
            var stale = running.CaptureEventDelivery(JObject.Parse("{type:'agent_settled',aborted:false}"));
            await CallWithoutContext(pane, "RebindAsync");
            var status = view.StatusText;
            var count = view.Activity.Count;
            await Task.Run(stale);
            await FlushUi(pane);
            Check(view.Activity.Count == count && view.StatusText == status, "Old-session settlement overwrote rebound status.");
        }
    }

    private static async Task VerifyPromptDispositionAsync()
    {
        Console.WriteLine("CASE: handled extension command settles only when Pi confirms idle; started and queued keep ownership.");
        foreach (var scenario in new[] { "handled-idle", "handled-streaming", "started", "queued" })
        {
            ResetFake();
            using (var pane = CreatePane())
            {
                var running = new PiSession
                {
                    PromptDisposition = scenario.StartsWith("handled") ? "handled" : scenario,
                    StateStreaming = scenario == "handled-streaming",
                    PromptGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                Set(pane, "session", running);
                var send = CallWithoutContext(pane, "SendAsync");
                await Task.Run(() => running.PromptGate.SetResult(true));
                await send;
                var completed = scenario == "handled-idle";
                Check(Field<bool>(pane, "busy") != completed && HasTerminal(View(pane), "done") == completed,
                    "Prompt disposition incorrectly completed or retained ownership: " + scenario);
            }
        }
    }

    private static async Task VerifyToolCallFormationAsync()
    {
        Console.WriteLine("CASE: tool-call formation forwards unknown names, repeated calls and complete fallback unchanged.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = await StartSessionAsync(pane);
            var view = View(pane);
            await EmitAsync(pane, running,
                "{type:'message_start',message:{role:'assistant',content:[]}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_start',contentIndex:0,id:'first',toolName:'future_private_tool'}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_start',contentIndex:0,id:'first',toolName:'future_private_tool'}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_delta',contentIndex:0,delta:'{\"range\":'}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_end',contentIndex:0,toolCall:{type:'toolCall',id:'first',name:'future_private_tool',arguments:{range:'A1'}}}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_start',contentIndex:1,id:'second',toolName:'future_private_tool'}}",
                "{type:'message_update',assistantMessageEvent:{type:'toolcall_end',contentIndex:1,toolCall:{type:'toolCall',id:'second',name:'future_private_tool',arguments:{range:'A2'}}}}",
                "{type:'message_end',message:{role:'assistant',stopReason:'toolUse',content:[{type:'toolCall',id:'fallback',name:'future_private_tool',arguments:{range:'A3'}}]}}");
            var starts = view.Activity.FindAll(item => (string)item["assistantMessageEvent"]?["type"] == "toolcall_start");
            Check(starts.Count == 3 && (string)starts[0]["assistantMessageEvent"]["id"] == "first" &&
                (string)starts[1]["assistantMessageEvent"]["id"] == "first" &&
                (string)starts[2]["assistantMessageEvent"]["id"] == "second", "Tool-call ids were filtered, merged or reordered by the controller.");
            Check((string)starts[2]["assistantMessageEvent"]["toolName"] == "future_private_tool",
                "An unknown tool name was filtered from activity.");
            var delta = view.Activity.Find(item => (string)item["assistantMessageEvent"]?["type"] == "toolcall_delta");
            Check((int?)delta?["assistantMessageEvent"]?["contentIndex"] == 0 &&
                (string)delta?["assistantMessageEvent"]?["delta"] == "{\"range\":", "Serialized argument delta was changed or dropped.");
            var ends = view.Activity.FindAll(item => (string)item["assistantMessageEvent"]?["type"] == "toolcall_end");
            Check(ends.Count == 2 && (string)ends[1]["assistantMessageEvent"]["toolCall"]["arguments"]["range"] == "A2",
                "Authoritative tool-call arguments were lost.");
            Check(view.Activity.Exists(item => (string)item["type"] == "message_end" &&
                (string)item["message"]?["content"]?[0]?["id"] == "fallback") &&
                !view.Transcript.Exists(text => text.StartsWith("Инструмент:")), "Fallback tool call was lost or duplicated as a plain message.");
        }
    }

    [STAThread]
    private static int Main()
    {
        var exitCode = 0;
        Control.CheckForIllegalCrossThreadCalls = true;
        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using (var deadline = new System.Windows.Forms.Timer { Interval = 30000 })
        {
            deadline.Tick += (sender, args) =>
            {
                Console.Error.WriteLine("FAIL: pane lifecycle harness exceeded its 30-second deadline.");
                exitCode = 1;
                Application.ExitThread();
            };
            EventHandler idle = null;
            idle = async (sender, args) =>
            {
                Application.Idle -= idle;
                try { await VerifyAsync(); }
                catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
                finally { deadline.Stop(); Application.ExitThread(); }
            };
            Application.Idle += idle;
            deadline.Start();
            Application.Run();
        }
        return exitCode;
    }
}
