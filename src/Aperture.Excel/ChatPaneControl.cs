using System;
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
        private WorkbookBinding binding;
        private PaneSettings settings;
        private PaneSettings chatSettings; // the script's provider/model, for this chat only
        private ScriptedRun scriptedRun;
        private PiSession session;
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
        internal void Stop() { RunCommand("stop", null); }

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
                        case "rebind": await RebindAsync(); break;
                        case "new_chat": await ResetChatAsync(); break;
                        case "settings": if (!busy) ShowSettings(); break;
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Write("pane_command_failed", ex.GetType().Name);
                    await OnUiAsync(() => { runError = ex.Message; EndRun("error"); SetStatus(ex.Message, true); });
                }
            }));
        }

        private void TryCaptureWorkbook()
        {
            try
            {
                binding = captureWorkbook();
                view.SetBinding(binding.Target.WorkbookName);
                SetStatus("Готова к работе");
            }
            catch (Exception ex)
            {
                binding = null;
                view.SetBinding(null);
                SetStatus(ex.Message, true);
            }
        }

        private async Task SendAsync(string text)
        {
            if (disposed || busy || String.IsNullOrWhiteSpace(text)) return;
            await SubmitAsync(text.Trim(), ++operationVersion, false);
        }

        // The automation's Start, on Excel's UI thread: a new chat with the script's provider and
        // model, then the prompt as if typed. Refusals throw before anything starts; the request
        // itself runs asynchronously and reports to run.
        internal void StartScripted(string text, string provider, string model, ScriptedRun run)
        {
            if (disposed || !IsHandleCreated) throw new InvalidOperationException("Панель Aperture2 этой книги не открыта: окно Excel должно быть видимым.");
            if (busy) throw new InvalidOperationException("Чат этой книги занят: дождитесь конца запроса или остановите его.");
            if (BindingValidationError() != null) TryCaptureWorkbook();
            Admit();
            scriptedRun = run;
            chatSettings = String.IsNullOrWhiteSpace(provider) && String.IsNullOrWhiteSpace(model) ? null : settings.WithModel(provider, model);
            runError = null;
            runStopped = false;
            view.SetScripted(true);
            SubmitAsync(text.Trim(), ++operationVersion, true).ContinueWith(
                task => Diagnostics.Write("scripted_request_failed", task.Exception.GetBaseException().GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private void Admit()
        {
            if (binding == null) throw new InvalidOperationException("Откройте книгу и нажмите «Привязать».");
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
                if (scripted || session == null || !session.IsRunning)
                {
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
                        SetStatus("Подключаю агента…");
                    });
                    if (!shouldStart) return;
                    var started = await PiSession.StartAsync(options, target, pendingStart.Token).ConfigureAwait(false);
                    var installed = false;
                    try
                    {
                        await OnUiAsync(() =>
                        {
                            if (!IsCurrentOperation(operation) || pendingStart.IsCancellationRequested) return;
                            session = started;
                            session.EventReceived += SessionEvent;
                            session.StandardError += SessionDiagnostic;
                            if (scriptedRun != null) scriptedRun.LogPath = session.SessionFile;
                            installed = true;
                            Diagnostics.Write("pi_session_started");
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
                    SetStatus("Думаю…");
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
                            if ((bool?)state["data"]?["isStreaming"] == false)
                            { EndRun("done"); SetStatus("Команда выполнена"); }
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() =>
                {
                    if (IsCurrentOperation(operation)) { EndRun("stopped"); SetStatus("Запуск отменён · запрос не отправлен"); }
                });
            }
            catch (Exception ex)
            {
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation)) return;
                    view.AppendMessage("Ошибка", ex.Message);
                    if (ex is TimeoutException && session != null && session.IsRunning)
                    {
                        SetStatus("Статус запроса неизвестен · остановите агента перед повтором", true);
                        view.SetBusy(true, true);
                    }
                    else { runError = ex.Message; EndRun("error"); SetStatus("Не удалось выполнить запрос", true); }
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
            SetStatus("Останавливаю…");
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
                    if ((bool?)state["data"]?["isStreaming"] == false)
                    { EndRun("stopped"); SetStatus("Ответ остановлен"); }
                });
            }
            catch (Exception ex)
            {
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                    view.AppendMessage("Ошибка остановки", ex.Message);
                    if (!current.IsRunning) { runError = ex.Message; EndRun("error"); SetStatus("Агент остановлен с ошибкой", true); }
                    else { view.SetBusy(true, true); SetStatus("Остановка не подтверждена · дождитесь завершения", true); }
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
                view.AppendMessage("Система", "Агент не остановился за " + forcedStopDelay.TotalSeconds + " с, его процесс завершён. Следующий запрос начнёт новую сессию.");
                EndRun("stopped");
                SetStatus("Ответ остановлен · сессия завершена");
            }), TaskScheduler.Default);
        }

        private async Task RebindAsync()
        {
            if (disposed || busy) return;
            var operation = BeginTransition();
            try
            {
                await DisposeSessionAsync().ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOperation(operation)) return;
                    TryCaptureWorkbook();
                    if (binding != null) view.AppendMessage("Система", "Книга привязана. Начинаем отдельную сессию.");
                });
            }
            catch (Exception ex) { await OnUiAsync(() => { if (IsCurrentOperation(operation)) SetStatus(ex.Message, true); }); }
            finally { await OnUiAsync(() => EndTransition(operation)); }
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
                    SetStatus("Готова к новому запросу");
                });
            }
            catch (Exception ex) { await OnUiAsync(() => { if (IsCurrentOperation(operation)) SetStatus(ex.Message, true); }); }
            finally { await OnUiAsync(() => EndTransition(operation)); }
        }

        private bool IsCurrentOperation(int operation) { return !disposed && operation == operationVersion; }
        private int BeginTransition()
        {
            var operation = ++operationVersion;
            transitioning = true;
            chatSettings = null; // a new chat or binding returns to the saved settings
            startupCancellation?.Cancel();
            SetBusy(true);
            view.SetBusy(true, false);
            SetStatus("Завершаю предыдущую сессию…");
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

        private void SessionDiagnostic(object sender, PiDiagnosticEventArgs args)
        { OnUi(() => { if (ReferenceEquals(sender, session)) SetStatus(args.Message, true); }); }

        private void SessionEvent(object sender, PiEvent args)
        {
            OnUi(() =>
            {
                if (!ReferenceEquals(sender, session)) return;
                var json = args.Json;
                // The view builds one chronological timeline from the raw events: text,
                // reasoning and tool calls keep their content positions. message_end is
                // authoritative for the parts the delta-only stream left incomplete.
                if (IsActivityEvent(args.Type)) view.ApplyActivity(json);
                if (args.Type == "message_end")
                {
                    var message = json["message"];
                    if ((string)message?["role"] == "assistant")
                    {
                        var error = (string)message["errorMessage"];
                        var stopReason = (string)message["stopReason"];
                        if (stopReason == "aborted")
                        {
                            runStopped = true;
                            SetStatus("Ответ остановлен");
                        }
                        else if (stopReason == "error" || !String.IsNullOrWhiteSpace(error))
                        {
                            // The timeline shows the provider error at its position in the run.
                            runError = String.IsNullOrWhiteSpace(error) ? "Провайдер вернул ошибку" : error;
                            SetStatus("Ошибка ответа · ожидаю завершения или повтора", true);
                        }
                        else
                        {
                            // Pi can retry a failed provider response within the same run.
                            // The latest successful assistant response supersedes that failure.
                            runError = null;
                            runStopped = false;
                        }
                    }
                }
                else if (args.Type == "tool_execution_start")
                { SetStatus("Работаю с книгой…"); }
                else if (args.Type == "tool_execution_end")
                {
                    SetStatus("Обрабатываю результат…");
                }
                else if (args.Type == "auto_retry_start")
                { SetStatus("Провайдер недоступен · Pi повторяет запрос…"); }
                else if (args.Type == "auto_retry_end" && (bool?)json["success"] == false)
                {
                    var error = (string)json["finalError"];
                    if (!String.IsNullOrWhiteSpace(error)) runError = error;
                }
                else if (args.Type == "agent_settled")
                {
                    if ((bool?)json["aborted"] == true || runStopped)
                    { EndRun("stopped"); SetStatus("Ответ остановлен"); }
                    else if (!String.IsNullOrWhiteSpace(runError))
                    { EndRun("error"); SetStatus("Запрос завершился ошибкой", true); }
                    else { activityOwned = false; EndRun("done"); SetStatus("Готово"); } // the view ends its card on agent_settled
                }
                else if (args.Type == "process_exit" || args.Type == "error")
                { runError = "Агент остановлен или вернул ошибку"; EndRun("error"); SetStatus(runError, true); }
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
                { settings = dialog.Settings; SetStatus("Настройки сохранены · начните новый чат"); }
        }
        private void SetBusy(bool value)
        {
            busy = value;
            if (!value) releaseTurn();
            view.SetBusy(value);
        }
        private void SetStatus(string text, bool error = false) { view.SetStatus(text, error); }

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
