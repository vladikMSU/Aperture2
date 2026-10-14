using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Aperture.Excel;
using Aperture.Runtime;

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
        return (Task)instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null);
    }

    private static ChatPaneControl CreatePane()
    {
        var pane = new ChatPaneControl(() => new WorkbookBinding(), () => true, () => { });
        var handle = pane.Handle; // Creates a hidden native control for WinForms dispatch.
        Field<TextBox>(pane, "prompt").Text = "audit";
        return pane;
    }

    private static void ResetFake()
    {
        PiSession.Starts = 0;
        PiSession.Prompts = 0;
        PiSession.StartGate = new TaskCompletionSource<PiSession>();
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
        Console.WriteLine("PASS: " + assertions + " pane lifecycle assertions (actual ChatPaneControl; fake Pi/COM dependencies; no Excel/model).");
    }

    [STAThread]
    private static int Main()
    {
        var exitCode = 0;
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
