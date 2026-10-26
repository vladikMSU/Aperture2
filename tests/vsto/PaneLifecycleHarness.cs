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
        PiSession.Resumes = 0;
        PiSession.Prompts = 0;
        PiSession.LastPromptThreadId = 0;
        PiSession.LastResumeFile = null;
        PiSession.LastTarget = null;
        PiSession.StartGate = new TaskCompletionSource<PiSession>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool HasNotice(ChatPaneView view, string level, string text, bool? inRun = null)
    {
        return view.Notices.Exists(item => item.Level == level && item.Text.Contains(text) && (inRun == null || item.InRun == inRun));
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

        foreach (var method in new[] { "ResetChatAsync" })
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
        await VerifyScriptedRunAsync();
        await VerifyRenamedBookAsync();
        await VerifyNoticesAsync();
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
            Check(View(pane).Activity.Exists(item => (string)item["assistantMessageEvent"]?["delta"] == "worker response"),
                "Background RPC text was not forwarded to the timeline.");
            Check(View(pane).Notices.Count == 0, "A Pi stderr line was shown in the timeline while the request continued.");
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
            Check(HasNotice(View(pane), "error", "test startup failure", false), "Startup failure was not shown as its own line.");
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
            Check(HasNotice(View(pane), "info", "Запуск отменён · запрос не отправлен", false), "A Stop during startup did not say that nothing was sent.");
        }

        foreach (var method in new[] { "ResetChatAsync" })
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
            Check(HasNotice(View(pane), "error", "test prompt rejection", true), "Prompt response failure was not shown in the request's card.");
            Check(HasTerminal(View(pane), "error"), "Prompt response failure did not end the request's card.");
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
            Check(HasNotice(View(pane), "warning", "Статус запроса неизвестен", true), "The unknown request status was not shown in its card.");
            var abort = CallWithoutContext(pane, "AbortAsync");
            Check(!View(pane).CanStop, "Duplicate Stop was not disabled while abort was pending.");
            await Task.Run(() => running.AbortGate.SetResult(true));
            await abort;
            Check(!Field<bool>(pane, "busy"), "Confirmed abort did not release the turn without a context.");
            Check(HasTerminal(View(pane), "stopped"), "Confirmed abort did not end the active timeline as stopped.");
            Check(!View(pane).Notices.Exists(item => item.Level == "error"), "A confirmed abort was reported as an error.");
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
            Check(HasNotice(View(pane), "warning", "Остановка не подтверждена · дождитесь завершения · test abort failure", true),
                "Abort failure was not rendered in the request's card on the UI thread.");
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
            Check(view.Activity.FindAll(item => (string)item["type"] == "message_update").Count == 5,
                "Every reasoning and text delta must reach the timeline in arrival order.");
            Check((string)view.Activity.FindAll(item => (string)item["type"] == "message_update")[3]["assistantMessageEvent"]?["delta"] == "answer",
                "Text deltas must keep their position between reasoning and tool events.");
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
            Check(view.Activity.Exists(item => (string)item["type"] == "message_end" &&
                (string)item.SelectToken("message.content[1].text") == "final answer"),
                "Authoritative final text must reach the timeline to reconcile an incomplete delta stream.");
            Check(Field<bool>(pane, "busy"), "agent_end incorrectly settled the run before Pi's terminal event.");
            await EmitAsync(pane, running, "{type:'agent_settled',aborted:false}");
            Check(!Field<bool>(pane, "busy") && view.Notices.Count == 0,
                "A successful settled run did not return to idle without notices.");

            var stale = running.CaptureEventDelivery(JObject.Parse("{type:'tool_execution_start',toolCallId:'stale',toolName:'wrong-session'}"));
            var completedActivityCount = view.Activity.Count;
            var lostBinding = Field<WorkbookBinding>(pane, "binding");
            lostBinding.ValidationFailure = "preflight binding failed";
            await CallWithoutContext(pane, "SendAsync");
            Check(view.Activity.Count == completedActivityCount,
                "A preflight error rewrote the previous completed activity.");
            Check(HasNotice(view, "error", "preflight binding failed", false), "A preflight binding error was not shown as its own line.");
            Check(ReferenceEquals(Field<WorkbookBinding>(pane, "binding"), lostBinding), "A failed send replaced the chat's book.");

            await CallWithoutContext(pane, "ResetChatAsync");
            Check(view.Activity.Count == 0, "New chat did not clear old activity.");
            Check(!ReferenceEquals(Field<WorkbookBinding>(pane, "binding"), lostBinding) && Field<WorkbookBinding>(pane, "binding").ValidationFailure == null,
                "New chat did not bind the window's book again after the chat lost it.");
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
            Check(HasTerminal(view, "error") && !Field<bool>(pane, "busy"), "Failed provider run left activity running.");
            Check(view.Notices.Count == 0, "The provider error, shown by the card itself, was repeated as a notice.");
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
            Check(view.Activity.FindAll(item => (string)item["type"] == "aperture_run_end").Count == 1 && !Field<bool>(pane, "busy"),
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
            Check(!HasTerminal(view, "error") && view.Notices.Count == 0, "Aborted settlement became a failure.");
            await CallWithoutContext(pane, "SendAsync");
            await Task.Run(() => running.EmitDiagnostic("Pi exited."));
            await EmitAsync(pane, running,
                "{type:'tool_execution_start',toolCallId:'interrupted',toolName:'excel_target',args:{}}",
                "{type:'process_exit'}");
            Check(HasTerminal(view, "error") && !Field<bool>(pane, "busy"), "Process exit left activity running.");
            Check(HasNotice(view, "error", "Агент остановлен или вернул ошибку · Pi exited.", true),
                "An agent that ended during the request was not explained in its card with Pi's last stderr line.");
            Check(view.Activity.Exists(item => (string)item["type"] == "process_exit"), "Process-exit lifecycle event was not forwarded.");
            var stale = running.CaptureEventDelivery(JObject.Parse("{type:'agent_settled',aborted:false}"));
            await CallWithoutContext(pane, "ResetChatAsync");
            var notices = view.Notices.Count;
            var count = view.Activity.Count;
            await Task.Run(stale);
            await FlushUi(pane);
            Check(view.Activity.Count == count && view.Notices.Count == notices, "Old-session settlement reached the new chat.");
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

    private static async Task UntilAsync(Control pane, Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception(message);
            await Task.Delay(20);
            await FlushUi(pane);
        }
    }

    private static async Task<PiSession> StartScriptedAsync(ChatPaneControl pane, ScriptedRun run)
    {
        pane.StartScripted("  scripted audit  ", "routerai", "deepseek/test", "high", run);
        var running = new PiSession();
        await Task.Run(() => PiSession.StartGate.SetResult(running));
        await UntilAsync(pane, () => PiSession.Prompts == 1, "The scripted prompt was not sent.");
        return running;
    }

    private static async Task VerifyScriptedRunAsync()
    {
        Console.WriteLine("CASE: automation Start begins a new chat with its own model, locks the composer and reports done.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var view = View(pane);
            var old = new PiSession();
            Set(pane, "session", old);
            view.AppendMessage("Вы", "earlier question");
            var run = new ScriptedRun();
            pane.StartScripted("  scripted audit  ", "routerai", "deepseek/test", "high", run);
            Check(Field<bool>(pane, "busy") && view.Scripted && run.State == "running", "Start did not take the chat synchronously.");
            var refused = false;
            try { pane.StartScripted("second", null, null, null, new ScriptedRun()); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "A busy chat accepted a second scripted Start.");
            var running = new PiSession();
            await Task.Run(() => PiSession.StartGate.SetResult(running));
            await UntilAsync(pane, () => PiSession.Prompts == 1, "The scripted prompt was not sent.");
            Check(old.Disposed, "Start did not end the previous chat session.");
            var options = (PaneSettings)PiSession.LastOptions;
            Check(options.Provider == "routerai" && options.Model == "deepseek/test" && options.Thinking == "high",
                "The script's provider, model and thinking level did not reach Pi.");
            Check(view.Transcript.Count == 1 && view.Transcript[0] == "Вы: scripted audit", "Start did not open a new chat with the typed prompt.");
            Check(!view.PromptCleared, "Start cleared a draft typed in the pane.");
            Check(run.LogPath == "history.jsonl", "The chat's Pi history path was not reported.");
            Check(run.Thinking == "high", "The level Pi reported after startup was not kept for GetState.");
            await EmitAsync(pane, running, "{type:'agent_settled',aborted:false}");
            Check(run.State == "done" && run.Error == null && !view.Scripted && !Field<bool>(pane, "busy"),
                "A settled scripted request was not reported as done.");

            await CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Prompts == 2 && PiSession.Starts == 1, "A follow-up in the scripted chat did not reuse its session.");
            await EmitAsync(pane, running, "{type:'agent_settled',aborted:false}");
            Check(run.State == "done", "A later request rewrote the finished scripted run.");
            ResetFake();
            await CallWithoutContext(pane, "ResetChatAsync");
            await StartSessionAsync(pane);
            Check(((PaneSettings)PiSession.LastOptions).Provider == null && ((PaneSettings)PiSession.LastOptions).Thinking == null,
                "The script's model or thinking level outlived its chat.");
        }

        Console.WriteLine("CASE: a thinking level alone keeps the saved model; the chat keeps its level when the renamed book resumes.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var run = new ScriptedRun();
            Check(run.Thinking == null, "A run reported a thinking level before Pi started.");
            pane.StartScripted("audit", "", "", "max", run);
            var first = new PiSession { ThinkingLevel = "max" };
            await Task.Run(() => PiSession.StartGate.SetResult(first));
            await UntilAsync(pane, () => PiSession.Prompts == 1, "The scripted prompt was not sent.");
            var options = (PaneSettings)PiSession.LastOptions;
            Check(options.Provider == null && options.Model == null && options.Thinking == "max" && run.Thinking == "max",
                "A thinking level without provider and model did not keep the saved model.");
            await EmitAsync(pane, first, "{type:'agent_settled',aborted:false}");
            Field<WorkbookBinding>(pane, "binding").RenamedTo = "Модель.xlsx";
            ResetFake();
            var send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Resumes == 1 && ((PaneSettings)PiSession.LastOptions).Thinking == "max", "The resumed chat lost its thinking level.");
            await Task.Run(() => PiSession.StartGate.SetResult(new PiSession { ThinkingLevel = "max" }));
            await send;
        }

        Console.WriteLine("CASE: automation Start refused while another window runs; startup failure reports failed.");
        ResetFake();
        using (var pane = new ChatPaneControl(() => new WorkbookBinding(), () => false, () => { }))
        {
            CreateHandles(pane);
            var refused = false;
            try { pane.StartScripted("audit", null, null, null, new ScriptedRun()); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && !Field<bool>(pane, "busy") && !View(pane).Scripted, "Start ran while another window held the turn.");
        }
        using (var pane = CreatePane())
        {
            var run = new ScriptedRun();
            pane.StartScripted("audit", null, null, null, run);
            await Task.Run(() => PiSession.StartGate.SetException(new InvalidOperationException("test startup failure")));
            await UntilAsync(pane, () => run.State != "running", "A failed scripted startup stayed running.");
            Check(run.State == "failed" && run.Error == "test startup failure" && !View(pane).Scripted, "Startup failure was not reported as failed.");
        }

        Console.WriteLine("CASE: automation Cancel is the Stop button; an unresponsive Pi is ended after the stop budget.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var run = new ScriptedRun();
            var running = await StartScriptedAsync(pane, run);
            pane.Stop();
            await UntilAsync(pane, () => run.State != "running", "Cancel did not end the scripted request.");
            Check(run.State == "cancelled" && !running.Disposed && !Field<bool>(pane, "busy"), "A confirmed Stop was not reported as cancelled.");
        }
        ResetFake();
        var delay = typeof(ChatPaneControl).GetField("forcedStopDelay", BindingFlags.Static | BindingFlags.NonPublic);
        var budget = delay.GetValue(null);
        delay.SetValue(null, TimeSpan.FromMilliseconds(200));
        try
        {
            using (var pane = CreatePane())
            {
                var run = new ScriptedRun();
                var running = await StartScriptedAsync(pane, run);
                running.AbortGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pane.Stop();
                await UntilAsync(pane, () => run.State != "running", "An unanswered Stop never ended the request.");
                Check(run.State == "cancelled" && running.Disposed && !Field<bool>(pane, "busy") && Field<PiSession>(pane, "session") == null,
                    "The forced stop did not end this chat's Pi.");
                Check(HasNotice(View(pane), "warning", "Агент не остановился", true), "The forced stop was not explained in the request's card.");
                running.AbortGate.SetResult(true);
            }
        }
        finally { delay.SetValue(null, budget); }
        await VerifyScriptedPaneOpeningAsync();
    }

    private static ChatPaneControl CreatePaneWithoutWindow()
    {
        // A pane just added to Excel: the control exists, its window does not yet.
        return new ChatPaneControl(() => new WorkbookBinding(), () => true, () => { });
    }

    private static async Task VerifyScriptedPaneOpeningAsync()
    {
        Console.WriteLine("CASE: automation Start on a pane without its window waits for it, then sends; Cancel and timeout end the wait.");
        ResetFake();
        using (var pane = CreatePaneWithoutWindow())
        {
            var run = new ScriptedRun();
            pane.StartScripted("audit", null, null, null, run);
            Check(!pane.IsHandleCreated && run.State == "running" && Field<bool>(pane, "busy") && PiSession.Starts == 0,
                "Start did not admit the request while the new pane had no window.");
            CreateHandles(pane);
            await UntilAsync(pane, () => PiSession.Starts == 1, "The request did not start once the pane got its window.");
            await Task.Run(() => PiSession.StartGate.SetResult(new PiSession()));
            await UntilAsync(pane, () => PiSession.Prompts == 1, "The scripted prompt was not sent after the pane opened.");
            Check(run.State == "running" && View(pane).Transcript.Count == 1, "The opened pane did not show the scripted request.");
        }
        ResetFake();
        using (var pane = CreatePaneWithoutWindow())
        {
            var run = new ScriptedRun();
            pane.StartScripted("audit", null, null, null, run);
            pane.Stop();
            Check(run.State == "cancelled" && !Field<bool>(pane, "busy") && !View(pane).Scripted, "Cancel while the pane opened did not end the request.");
            CreateHandles(pane);
            await FlushUi(pane);
            Check(PiSession.Starts == 0 && PiSession.Prompts == 0, "A cancelled request started when the pane opened.");
        }
        ResetFake();
        var timeout = typeof(ChatPaneControl).GetField("paneOpenTimeout", BindingFlags.Static | BindingFlags.NonPublic);
        var budget = timeout.GetValue(null);
        timeout.SetValue(null, TimeSpan.FromMilliseconds(200));
        try
        {
            using (var pane = CreatePaneWithoutWindow())
            {
                var run = new ScriptedRun();
                pane.StartScripted("audit", null, null, null, run);
                // The pane never gets its window; its timer runs on this thread's message loop.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (run.State == "running" && DateTime.UtcNow < deadline) await Task.Delay(20);
                Check(run.State == "failed" && run.Error.Contains("не открылась") && !Field<bool>(pane, "busy") && PiSession.Starts == 0,
                    "A pane that never opened left the scripted request running.");
            }
        }
        finally { timeout.SetValue(null, budget); }
    }

    private static async Task VerifyRenamedBookAsync()
    {
        Console.WriteLine("CASE: after Save As the next request continues the same Pi history with a Pi/MCP bound to the new name.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var view = View(pane);
            var first = await StartSessionAsync(pane);
            await EmitAsync(pane, first, "{type:'agent_settled',aborted:false}");
            var workbookId = pane.BoundWorkbookId;
            Field<WorkbookBinding>(pane, "binding").RenamedTo = "Модель.xlsx";
            ResetFake();
            var send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Resumes == 1 && PiSession.Starts == 0 && PiSession.LastResumeFile == "history.jsonl",
                "The renamed book did not continue the chat's Pi history.");
            Check(first.Disposed, "The Pi/MCP bound to the old name was not stopped before the restart.");
            Check(PiSession.LastTarget.WorkbookName == "Модель.xlsx" && PiSession.LastTarget.WorkbookId == workbookId,
                "The restarted MCP did not get the same book under its new name.");
            var resumed = new PiSession();
            await Task.Run(() => PiSession.StartGate.SetResult(resumed));
            await send;
            Check(PiSession.Prompts == 1 && pane.BoundWorkbookId == workbookId, "The follow-up was not sent to the continued chat.");
            Check(view.Transcript.Count == 2 && view.Notices.Count == 0, "Continuing the chat cleared it or announced the rebinding.");
            await EmitAsync(pane, resumed, "{type:'agent_settled',aborted:false}");
            ResetFake();
            await CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Starts == 0 && PiSession.Resumes == 0 && PiSession.Prompts == 1, "An unchanged book restarted the continued Pi.");
            await EmitAsync(pane, resumed, "{type:'agent_settled',aborted:false}");
        }

        Console.WriteLine("CASE: a failed continuation is retried by the next request; New chat starts afresh.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var view = View(pane);
            var first = await StartSessionAsync(pane);
            await EmitAsync(pane, first, "{type:'agent_settled',aborted:false}");
            Field<WorkbookBinding>(pane, "binding").RenamedTo = "Модель.xlsx";
            ResetFake();
            var send = CallWithoutContext(pane, "SendAsync");
            await Task.Run(() => PiSession.StartGate.SetException(new InvalidOperationException("test resume failure")));
            await send;
            Check(HasNotice(view, "error", "test resume failure", false) && !Field<bool>(pane, "busy") && PiSession.Prompts == 0,
                "A failed continuation was not reported or still sent the prompt.");
            ResetFake();
            send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Resumes == 1 && PiSession.Starts == 0 && PiSession.LastResumeFile == "history.jsonl",
                "The next request did not retry the chat's history.");
            await Task.Run(() => PiSession.StartGate.SetCanceled());
            await send;
            ResetFake();
            await CallWithoutContext(pane, "ResetChatAsync");
            await StartSessionAsync(pane);
            Check(PiSession.Starts == 1 && PiSession.Resumes == 0, "New chat continued the old history.");
        }

        Console.WriteLine("CASE: a renamed book whose agent already ended starts a new session and says why.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var view = View(pane);
            var first = await StartSessionAsync(pane);
            await EmitAsync(pane, first, "{type:'agent_settled',aborted:false}");
            first.Dispose(); // the fake Pi is not running once disposed
            Field<WorkbookBinding>(pane, "binding").RenamedTo = "Модель.xlsx";
            ResetFake();
            var send = CallWithoutContext(pane, "SendAsync");
            Check(PiSession.Starts == 1 && PiSession.Resumes == 0, "An ended agent was resumed instead of starting a new session.");
            Check(HasNotice(view, "info", "Книга теперь называется «Модель.xlsx»", false), "The new session after the rename was not explained.");
            await Task.Run(() => PiSession.StartGate.SetResult(new PiSession()));
            await send;
            Check(PiSession.Prompts == 1, "The request after the rename was not sent.");
        }

        Console.WriteLine("CASE: automation Start on a renamed book binds it anew in a new chat.");
        ResetFake();
        using (var pane = CreatePane())
        {
            var first = await StartSessionAsync(pane);
            await EmitAsync(pane, first, "{type:'agent_settled',aborted:false}");
            var workbookId = pane.BoundWorkbookId;
            Field<WorkbookBinding>(pane, "binding").RenamedTo = "Модель.xlsx";
            ResetFake();
            var run = new ScriptedRun();
            pane.StartScripted("audit", null, null, null, run);
            await Task.Run(() => PiSession.StartGate.SetResult(new PiSession()));
            await UntilAsync(pane, () => PiSession.Prompts == 1, "The scripted prompt was not sent.");
            Check(PiSession.Starts == 1 && PiSession.Resumes == 0 && pane.BoundWorkbookId != workbookId,
                "A scripted Start on a renamed book did not bind it anew in a new chat.");
        }
    }

    private static async Task VerifyNoticesAsync()
    {
        Console.WriteLine("CASE: refusals, failed capture and an agent that ends between requests are their own lines; progress is not reported.");
        ResetFake();
        using (var pane = new ChatPaneControl(() => new WorkbookBinding(), () => false, () => { }))
        {
            CreateHandles(pane);
            await CallWithoutContext(pane, "SendAsync");
            await CallWithoutContext(pane, "SendAsync");
            Check(View(pane).Notices.Count == 2 && HasNotice(View(pane), "error", "В другом окне Excel уже выполняется запрос Aperture2", false)
                && PiSession.Starts == 0, "A request refused for another window was not shown as its own line.");
        }
        ResetFake();
        using (var pane = CreatePane())
        {
            var running = await StartSessionAsync(pane);
            await EmitAsync(pane, running,
                "{type:'tool_execution_start',toolCallId:'a',toolName:'excel_target',args:{}}",
                "{type:'tool_execution_end',toolCallId:'a',toolName:'excel_target',result:{},isError:false}",
                "{type:'auto_retry_start',attempt:1,maxAttempts:3}",
                "{type:'auto_retry_end',success:true,attempt:1}",
                "{type:'agent_settled',aborted:false}");
            Check(View(pane).Notices.Count == 0 && View(pane).Activity.Exists(item => (string)item["type"] == "auto_retry_start"),
                "Progress was reported as notices, or the retry did not reach the card.");
            await Task.Run(() => running.EmitDiagnostic("fatal: worker crashed"));
            await EmitAsync(pane, running, "{type:'process_exit'}");
            Check(HasNotice(View(pane), "error", "Агент остановлен или вернул ошибку · fatal: worker crashed", false),
                "An agent that ended between requests was not reported on its own line.");
        }
        ResetFake();
        using (var pane = new ChatPaneControl(() => { throw new InvalidOperationException("Окно больше не связано с открытой книгой."); }, () => true, () => { }))
        {
            CreateHandles(pane);
            Check(HasNotice(View(pane), "error", "Окно больше не связано", false), "A failed capture at pane creation was not shown.");
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
