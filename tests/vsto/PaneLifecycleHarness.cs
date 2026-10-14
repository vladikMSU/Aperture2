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
            Set(pane, "assistantStreaming", true);
            var abort = CallWithoutContext(pane, "AbortAsync");
            Check(!View(pane).CanStop, "Duplicate Stop was not disabled while abort was pending.");
            await Task.Run(() => running.AbortGate.SetResult(true));
            await abort;
            Check(!Field<bool>(pane, "busy"), "Confirmed abort did not release the turn without a context.");
            Check(!Field<bool>(pane, "assistantStreaming"), "Confirmed abort kept the prior assistant stream open.");
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
