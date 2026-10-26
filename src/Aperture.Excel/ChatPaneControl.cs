using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Aperture.Runtime;
using Newtonsoft.Json.Linq;

namespace Aperture.Excel
{
    internal sealed class ChatPaneControl : UserControl
    {
        private Func<WorkbookBinding> captureWorkbook;
        private readonly Func<bool> acquireTurn;
        private readonly Action releaseTurn;
        private readonly ChatPaneView view = new ChatPaneView();
        private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly CancellationTokenSource dispatchLifetime = new CancellationTokenSource();
        private readonly Queue<string> diagnostics = new Queue<string>(); // recent Pi stderr of the current session
        private WorkbookBinding binding;
        private PaneSettings settings;
        private PaneSettings chatSettings; // the script's provider/model, for this chat only
        private ScriptedRun scriptedRun;
        private PiSession session;
        private bool targetRenamed; // the book was renamed after the current Pi/MCP started
        private string resumeHistory; // Pi history this chat continues in its next session
        private Action cancelPaneWait;
        private bool busy;
        private string runError;
        private bool runStopped;
        private bool activityOwned;
        private volatile bool disposed;
        private bool transitioning;
        private int operationVersion;
        private CancellationTokenSource startupCancellation;
        // Pi's abort waits for running tools; past this budget Stop ends the chat's own Pi/MCP.
        private static TimeSpan forcedStopDelay = TimeSpan.FromSeconds(8);
        // A pane created for a scripted request gets its window when Excel shows it.
        private static TimeSpan paneOpenTimeout = TimeSpan.FromSeconds(30);

        internal string BoundWorkbookId { get { return binding == null ? null : binding.Target.WorkbookId; } }
        internal bool SessionRunning { get { return session != null && session.IsRunning; } }
        internal bool ViewReady { get { return view.IsReady; } }
        internal string ViewInitializationError { get { return view.InitializationError; } }
        internal string BindingValidationError()
        {
            if (disposed || binding == null) return "Книга не привязана";
            try { binding.Validate(); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        internal ChatPaneControl(Func<WorkbookBinding> captureWorkbook, Func<bool> acquireTurn, Action releaseTurn)
        {
            this.captureWorkbook = captureWorkbook;
            this.acquireTurn = acquireTurn;
            this.releaseTurn = releaseTurn;
            settings = PaneSettings.Load();
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(24, 26, 29);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(280, 350);
            view.Dock = DockStyle.Fill;
            Controls.Add(view);
            view.Command += ViewCommand;
            TryCaptureWorkbook();
        }

        private void ViewCommand(object sender, ChatPaneCommandEventArgs args) { RunCommand(args.Command, args.Text); }

        // The automation's Cancel is the Stop button.
        internal void Stop()
        {
            if (cancelPaneWait != null)
            {
                // The scripted request still waits for its pane: nothing was started or sent.
                cancelPaneWait();
                ++operationVersion;
                Notify("info", "Запуск отменён · запрос не отправлен");
                EndRun("stopped");
                return;
            }
            RunCommand("stop", null);
        }

        private void RunCommand(string command, string text)
        {
            if (disposed || !IsHandleCreated) return;
            // Leave the WebView callback before a command can open a modal settings dialog.
            BeginInvoke(new Action(async () =>
            {
                if (disposed) return;
                try
                {
                    switch (command)
                    {
                        case "send": await SendAsync(text); break;
                        case "stop": await AbortAsync(); break;
                        case "new_chat": await ResetChatAsync(); break;
                        case "settings": if (!busy) ShowSettings(); break;
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Write("pane_command_failed", ex.GetType().Name);
                    await OnUiAsync(() => { runError = ex.Message; Notify("error", ex.Message); EndRun("error"); });
                }
            }));
        }

        private void TryCaptureWorkbook()
        {
            try { binding = captureWorkbook(); }
            catch (Exception ex) { binding = null; Notify("error", ex.Message); }
        }

        private async Task SendAsync(string text)
        {
            if (disposed || busy || String.IsNullOrWhiteSpace(text)) return;
            await SubmitAsync(text.Trim(), ++operationVersion, false);
        }

        // The automation's Start, on Excel's UI thread: a new chat with the script's provider, model
        // and thinking level, then the prompt as if typed. Refusals throw before anything starts; the
        // request itself runs asynchronously and reports to run.
        internal void StartScripted(string text, string provider, string model, string thinking, ScriptedRun run)
        {
            if (disposed) throw new InvalidOperationException("Панель Aperture2 этой книги закрыта.");
            if (busy) throw new InvalidOperationException("Чат этой книги занят: дождитесь конца запроса или остановите его.");
            // A new chat always binds the book of this window anew, also after Save As.
            if (BindingValidationError() != null) { binding = null; binding = captureWorkbook(); }
            Admit();
            scriptedRun = run;
            chatSettings = String.IsNullOrWhiteSpace(provider) && String.IsNullOrWhiteSpace(model) && String.IsNullOrWhiteSpace(thinking)
                ? null : settings.ForChat(provider, model, thinking);
            resumeHistory = null;
            runError = null;
            runStopped = false;
            view.SetScripted(true);
            var operation = ++operationVersion;
            if (IsHandleCreated) SubmitScripted(text.Trim(), operation);
            else AwaitPaneHandle(text.Trim(), operation);
        }

        private void SubmitScripted(string text, int operation)
        {
            SubmitAsync(text, operation, true).ContinueWith(
                task => Diagnostics.Write("scripted_request_failed", task.Exception.GetBaseException().GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        // A pane created for this request may get its window only once Excel shows it. The request is
        // already admitted; it starts with the window or fails after the timeout, never silently waits.
        private void AwaitPaneHandle(string text, int operation)
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)paneOpenTimeout.TotalMilliseconds };
            EventHandler created = null;
            cancelPaneWait = () => { HandleCreated -= created; timer.Dispose(); cancelPaneWait = null; };
            created = (sender, args) =>
            {
                cancelPaneWait();
                // Leave handle creation before the request touches the view.
                BeginInvoke(new Action(() => { if (IsCurrentOperation(operation)) SubmitScripted(text, operation); }));
            };
            timer.Tick += (sender, args) =>
            {
                cancelPaneWait();
                if (!IsCurrentOperation(operation)) return;
                runError = "Панель Aperture2 не открылась за " + paneOpenTimeout.TotalSeconds + " с: окно Excel должно быть видимым.";
                Notify("error", runError);
                EndRun("error");
            };
            HandleCreated += created;
            timer.Start();
            Diagnostics.Write("scripted_waits_for_pane");
        }

        private void Admit()
        {
            // Without a book (its capture failed) the window's book is captured now. A book renamed by
            // its first save or Save As is followed: same book and window, same IDs, new name.
            if (binding == null) binding = captureWorkbook();
            else
            {
                var current = binding.FollowRename();
                if (!ReferenceEquals(current, binding)) { binding = current; targetRenamed = true; Diagnostics.Write("binding_followed_rename"); }
            }
            binding.Validate();
            if (!acquireTurn()) throw new InvalidOperationException("В другом окне Excel уже выполняется запрос Aperture2.");
            SetBusy(true);
        }

        // A scripted request was admitted by StartScripted and always begins a new chat.
        private async Task SubmitAsync(string text, int operation, bool scripted)
        {
            CancellationTokenSource pendingStart = null;
            try
            {
                if (!scripted) Admit();
                pendingStart = new CancellationTokenSource();
                startupCancellation = pendingStart;
                // The book was renamed since this Pi/MCP started: they still address the old name.
                var retarget = session != null && targetRenamed;
                if (scripted || retarget || resumeHistory != null || session == null || !session.IsRunning)
                {
                    // A live chat keeps its Pi history: the restarted Pi opens the same session file.
                    var restartedFromScratch = false;
                    if (!scripted && retarget)
                    {
                        if (session.IsRunning && !String.IsNullOrEmpty(session.SessionFile)) resumeHistory = session.SessionFile;
                        else restartedFromScratch = resumeHistory == null;
                    }
                    var resumeFile = scripted ? null : resumeHistory;
                    // Snapshot context on the owning thread; Office may provide no
                    // SynchronizationContext, so no code after await assumes UI affinity.
                    var options = (chatSettings ?? settings).ToOptions();
                    var target = binding.Target;
                    await DisposeSessionAsync().ConfigureAwait(false);
                    var shouldStart = false;
                    await OnUiAsync(() =>
                    {
                        if (!IsCurrentOperation(operation)) return;
                        pendingStart.Token.ThrowIfCancellationRequested();
                        shouldStart = true;
                        if (scripted) view.ClearTranscript();
                        if (restartedFromScratch)
                            Notify("info", "Книга теперь называется «" + target.WorkbookName + "». Агент этого чата уже был остановлен, поэтому разговор начинается заново.");
                    });
                    if (!shouldStart) return;
                    var started = resumeFile != null
                        ? await PiSession.ResumeAsync(options, target, resumeFile, pendingStart.Token).ConfigureAwait(false)
                        : await PiSession.StartAsync(options, target, pendingStart.Token).ConfigureAwait(false);
                    var installed = false;
                    try
                    {
                        await OnUiAsync(() =>
                        {
                            if (!IsCurrentOperation(operation) || pendingStart.IsCancellationRequested) return;
                            session = started;
                            targetRenamed = false;
                            resumeHistory = null;
                            diagnostics.Clear();
                            session.EventReceived += SessionEvent;
                            session.StandardError += SessionDiagnostic;
                            if (scriptedRun != null) { scriptedRun.LogPath = session.SessionFile; scriptedRun.Thinking = session.ThinkingLevel; }
                            installed = true;
                            Diagnostics.Write(resumeFile != null ? "pi_session_resumed" : "pi_session_started");
                        });
                    }
                    finally { if (!installed) started.Dispose(); }
                    if (!installed) { pendingStart.Token.ThrowIfCancellationRequested(); return; }
                }
                Task<JObject> submittedPrompt = null;
                PiSession promptSession = null;
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation)) return;
                    pendingStart.Token.ThrowIfCancellationRequested();
                    binding.Validate();
                    runError = null;
                    runStopped = false;
                    view.AppendMessage("Вы", text);
                    activityOwned = true;
                    if (!scripted) view.ClearPrompt(); // a draft typed in the pane is not the script's
                    // Admit the async RPC write before a subsequent UI Stop command.
                    // Only initiation occurs here; all network/process waiting stays async.
                    promptSession = session;
                    submittedPrompt = promptSession.SendPromptAsync(text);
                });
                if (submittedPrompt != null)
                {
                    var response = await submittedPrompt.ConfigureAwait(false);
                    // Extension commands such as /mcp are handled without an agent run,
                    // so Pi will not emit agent_settled for their completion.
                    if ((string)response["data"]?["disposition"] == "handled")
                    {
                        var state = await promptSession.GetStateAsync().ConfigureAwait(false);
                        await OnUiAsync(() =>
                        {
                            if (!IsCurrentOperation(operation) || !ReferenceEquals(promptSession, session)) return;
                            if ((bool?)state["data"]?["isStreaming"] == false) EndRun("done");
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() =>
                {
                    if (IsCurrentOperation(operation)) { Notify("info", "Запуск отменён · запрос не отправлен"); EndRun("stopped"); }
                });
            }
            catch (Exception ex)
            {
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation)) return;
                    if (ex is TimeoutException && session != null && session.IsRunning)
                    {
                        Notify("warning", "Статус запроса неизвестен · остановите агента перед повтором", ex.Message);
                        view.SetBusy(true, true);
                    }
                    else { runError = ex.Message; Notify("error", "Не удалось выполнить запрос", ex.Message); EndRun("error"); }
                });
                Diagnostics.Write("request_failed", ex.GetType().Name);
            }
            finally
            {
                await OnUiAsync(() => { if (ReferenceEquals(startupCancellation, pendingStart)) startupCancellation = null; });
                pendingStart?.Dispose();
            }
        }

        private async Task AbortAsync()
        {
            if (disposed || transitioning || !busy) return;
            var operation = operationVersion;
            Diagnostics.Write("abort_requested"); // Distinguishes a user Stop from other aborts in Pi history.
            startupCancellation?.Cancel();
            view.SetBusy(true, false);
            var current = session;
            if (current == null) return;
            ForceStopLater(current, operation);
            try
            {
                await current.AbortAsync().ConfigureAwait(false);
                var state = await current.GetStateAsync().ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                    if ((bool?)state["data"]?["isStreaming"] == false) EndRun("stopped");
                });
            }
            catch (Exception ex)
            {
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                    if (!current.IsRunning) { runError = ex.Message; Notify("error", "Агент остановлен с ошибкой", ex.Message); EndRun("error"); }
                    else { Notify("warning", "Остановка не подтверждена · дождитесь завершения", ex.Message); view.SetBusy(true, true); }
                });
            }
        }

        private void ForceStopLater(PiSession current, int operation)
        {
            Task.Delay(forcedStopDelay).ContinueWith(_ => OnUi(() =>
            {
                if (!busy || !IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                // Only this chat's Pi/MCP tree is terminated; Excel and its books stay untouched.
                Diagnostics.Write("stop_forced");
                session = null;
                current.EventReceived -= SessionEvent;
                current.StandardError -= SessionDiagnostic;
                current.Dispose();
                Notify("warning", "Агент не остановился за " + forcedStopDelay.TotalSeconds + " с, его процесс завершён. Следующий запрос начнёт новую сессию.");
                EndRun("stopped");
            }), TaskScheduler.Default);
        }

        private async Task ResetChatAsync()
        {
            if (disposed || busy) return;
            var operation = BeginTransition();
            try
            {
                await DisposeSessionAsync().ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation)) return;
                    view.ClearTranscript();
                    // A new chat binds this window's book again when the chat lost it.
                    if (BindingValidationError() != null) TryCaptureWorkbook();
                });
            }
            catch (Exception ex) { await OnUiAsync(() => { if (IsCurrentOperation(operation)) Notify("error", ex.Message); }); }
            finally { await OnUiAsync(() => EndTransition(operation)); }
        }

        private bool IsCurrentOperation(int operation) { return !disposed && operation == operationVersion; }
        private int BeginTransition()
        {
            var operation = ++operationVersion;
            transitioning = true;
            chatSettings = null; // a new chat returns to the saved settings
            resumeHistory = null;
            targetRenamed = false; // its session starts with the current book
            startupCancellation?.Cancel();
            SetBusy(true);
            view.SetBusy(true, false);
            return operation;
        }
        private void EndTransition(int operation)
        {
            if (!IsCurrentOperation(operation)) return;
            transitioning = false;
            SetBusy(false);
        }
        private async Task DisposeSessionAsync()
        {
            var previous = session;
            session = null;
            if (previous == null) return;
            previous.EventReceived -= SessionEvent;
            previous.StandardError -= SessionDiagnostic;
            try { await previous.StopAsync().ConfigureAwait(false); }
            finally { previous.Dispose(); }
        }

        // Pi's stderr is not shown line by line; the recent lines explain an agent that ended.
        private void SessionDiagnostic(object sender, PiDiagnosticEventArgs args)
        {
            OnUi(() =>
            {
                if (!ReferenceEquals(sender, session) || String.IsNullOrWhiteSpace(args.Message)) return;
                diagnostics.Enqueue(args.Message.Length > 300 ? args.Message.Substring(0, 300) + "…" : args.Message);
                while (diagnostics.Count > 4) diagnostics.Dequeue();
            });
        }

        private void SessionEvent(object sender, PiEvent args)
        {
            OnUi(() =>
            {
                if (!ReferenceEquals(sender, session)) return;
                var json = args.Json;
                // The view builds one chronological timeline from the raw events: text,
                // reasoning, tool calls, provider errors and retries keep their positions.
                // message_end is authoritative for the parts the delta-only stream left incomplete.
                if (IsActivityEvent(args.Type)) view.ApplyActivity(json);
                if (args.Type == "message_end")
                {
                    var message = json["message"];
                    if ((string)message?["role"] == "assistant")
                    {
                        var error = (string)message["errorMessage"];
                        var stopReason = (string)message["stopReason"];
                        if (stopReason == "aborted") runStopped = true;
                        // The timeline shows the provider error at its position in the run.
                        else if (stopReason == "error" || !String.IsNullOrWhiteSpace(error))
                            runError = String.IsNullOrWhiteSpace(error) ? "Провайдер вернул ошибку" : error;
                        else
                        {
                            // Pi can retry a failed provider response within the same run.
                            // The latest successful assistant response supersedes that failure.
                            runError = null;
                            runStopped = false;
                        }
                    }
                }
                else if (args.Type == "auto_retry_end" && (bool?)json["success"] == false)
                {
                    var error = (string)json["finalError"];
                    if (!String.IsNullOrWhiteSpace(error)) runError = error;
                }
                else if (args.Type == "agent_settled")
                {
                    if ((bool?)json["aborted"] == true || runStopped) EndRun("stopped");
                    else if (!String.IsNullOrWhiteSpace(runError)) EndRun("error");
                    else { activityOwned = false; EndRun("done"); } // the view ends its card on agent_settled
                }
                else if (args.Type == "process_exit" || args.Type == "error")
                {
                    runError = "Агент остановлен или вернул ошибку";
                    var detail = (string)json["message"] ?? (string)json["error"];
                    Notify("error", runError, String.IsNullOrWhiteSpace(detail) ? String.Join("\n", diagnostics) : detail);
                    EndRun("error");
                }
            });
        }

        private static bool IsActivityEvent(string type)
        {
            switch (type)
            {
                case "agent_start": case "agent_end": case "turn_start": case "turn_end":
                case "message_start": case "message_update": case "message_end":
                case "tool_execution_start": case "tool_execution_update": case "tool_execution_end":
                case "auto_retry_start": case "auto_retry_end":
                case "agent_settled": case "process_exit": case "error": return true;
                default: return false;
            }
        }

        // What the user has to know goes into the timeline: the card of the request in progress,
        // otherwise its own line. Progress is not reported here; the card shows it.
        private void Notify(string level, string title, string detail = null)
        {
            view.AppendNotice(level, title, String.IsNullOrWhiteSpace(detail) ? null : detail, activityOwned);
        }

        private void EndActivity(string status)
        {
            // Preflight/startup errors and idle chat transitions do not own the
            // previous completed activity card. Only a submitted user turn does.
            if (!activityOwned) return;
            if (status == "stopped") runStopped = true;
            view.ApplyActivity(new JObject { ["type"] = "aperture_run_end", ["status"] = status });
            activityOwned = false;
        }

        // Every end of a submitted request: done, stopped or error.
        private void EndRun(string status)
        {
            EndActivity(status);
            SetBusy(false);
            if (scriptedRun == null) return;
            scriptedRun.End(status, runError);
            scriptedRun = null;
            view.SetScripted(false);
        }

        private void ShowSettings()
        {
            using (var dialog = new SettingsDialog(settings))
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { settings = dialog.Settings; Notify("info", "Настройки сохранены · начните новый чат"); }
        }
        private void SetBusy(bool value)
        {
            busy = value;
            if (!value) releaseTurn();
            view.SetBusy(value);
        }

        // Only the delegate is guaranteed to run on the owner thread. Awaiting
        // this Task does not transfer the caller's continuation to that thread.
        private async Task OnUiAsync(Action action)
        {
            if (disposed) return;
            if (Thread.CurrentThread.ManagedThreadId == uiThreadId) { action(); return; }
            if (!IsHandleCreated) return;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (dispatchLifetime.Token.Register(() => completion.TrySetResult(false)))
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { if (!disposed) action(); completion.TrySetResult(true); }
                        catch (Exception ex) { completion.TrySetException(ex); }
                    }));
                }
                catch (InvalidOperationException) { completion.TrySetResult(false); }
                await completion.Task.ConfigureAwait(false);
            }
        }
        private void OnUi(Action action)
        {
            OnUiAsync(action).ContinueWith(task => Diagnostics.Write("ui_dispatch_failed", task.Exception.GetBaseException().GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                ++operationVersion;
                cancelPaneWait?.Invoke();
                dispatchLifetime.Cancel();
                startupCancellation?.Cancel();
                releaseTurn();
                if (scriptedRun != null) { scriptedRun.End("error", "Панель Aperture2 закрыта до конца запроса"); scriptedRun = null; }
                view.Command -= ViewCommand;
                if (session != null)
                {
                    session.EventReceived -= SessionEvent;
                    session.StandardError -= SessionDiagnostic;
                    session.Dispose(); session = null;
                }
                binding = null;
                captureWorkbook = null;
            }
            base.Dispose(disposing);
        }
    }
}
