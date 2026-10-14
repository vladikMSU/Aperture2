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
        private readonly Label workbookLabel = new Label();
        private readonly Label status = new Label();
        private readonly RichTextBox transcript = new RichTextBox();
        private readonly TextBox prompt = new TextBox();
        private readonly Button send = new Button();
        private readonly Button stop = new Button();
        private readonly Button bind = new Button();
        private readonly Button newChat = new Button();
        private readonly Button settingsButton = new Button();
        private WorkbookBinding binding;
        private PaneSettings settings;
        private PiSession session;
        private bool busy;
        private bool assistantStreaming;
        private bool disposed;
        private bool transitioning;
        private int operationVersion;
        private CancellationTokenSource startupCancellation;

        internal string BoundWorkbookId { get { return binding == null ? null : binding.Target.WorkbookId; } }
        internal bool SessionRunning { get { return session != null && session.IsRunning; } }
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
            BackColor = Color.FromArgb(247, 248, 250);
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(300, 350);
            BuildLayout();
            AppendMessage("Aperture2", "Задайте вопрос о модели или попросите проверить расчёты. Работа ведётся с указанной книгой; переключение окна её не меняет.");
            TryCaptureWorkbook();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.Controls.Add(new Label { Text = "Aperture2", Font = new Font("Segoe UI Semibold", 14F), AutoSize = true, ForeColor = Color.FromArgb(24, 50, 75) }, 0, 0);
            workbookLabel.Dock = DockStyle.Fill;
            workbookLabel.AutoEllipsis = true;
            workbookLabel.ForeColor = Color.FromArgb(59, 72, 85);
            layout.Controls.Add(workbookLabel, 0, 1);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            bind.Text = "Привязать"; newChat.Text = "Новый чат"; settingsButton.Text = "Настройки";
            foreach (var button in new[] { bind, newChat, settingsButton }) { button.AutoSize = true; button.Height = 28; toolbar.Controls.Add(button); }
            bind.Click += async (s, e) => await RebindAsync();
            newChat.Click += async (s, e) => await ResetChatAsync();
            settingsButton.Click += ShowSettings;
            layout.Controls.Add(toolbar, 0, 2);
            transcript.Dock = DockStyle.Fill; transcript.ReadOnly = true; transcript.BorderStyle = BorderStyle.FixedSingle;
            transcript.BackColor = Color.White; transcript.DetectUrls = false; transcript.Font = new Font("Segoe UI", 10F);
            layout.Controls.Add(transcript, 0, 3);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 4);
            prompt.Multiline = true; prompt.AcceptsReturn = true; prompt.Dock = DockStyle.Fill; prompt.ScrollBars = ScrollBars.Vertical;
            prompt.KeyDown += async (s, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); } };
            layout.Controls.Add(prompt, 0, 5);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            send.Text = "Отправить"; send.AutoSize = true; send.BackColor = Color.FromArgb(25, 91, 142); send.ForeColor = Color.White; send.FlatStyle = FlatStyle.Flat;
            stop.Text = "Стоп"; stop.AutoSize = true; stop.Enabled = false;
            send.Click += async (s, e) => await SendAsync(); stop.Click += async (s, e) => await AbortAsync();
            actions.Controls.Add(send); actions.Controls.Add(stop);
            actions.Controls.Add(new Label { Text = "Ctrl+Enter", AutoSize = true, Padding = new Padding(0, 6, 10, 0), ForeColor = Color.DimGray });
            layout.Controls.Add(actions, 0, 6);
            Controls.Add(layout);
            SetStatus("Готова к запуску Pi");
        }

        private void TryCaptureWorkbook()
        {
            try
            {
                binding = captureWorkbook();
                workbookLabel.Text = "Книга: " + binding.Target.WorkbookName + Environment.NewLine + "Привязана к этому чату";
                SetStatus("Книга привязана · Pi запускается при отправке");
            }
            catch (Exception ex)
            {
                binding = null;
                workbookLabel.Text = "Книга не привязана";
                SetStatus(ex.Message, true);
            }
        }

        private async Task SendAsync()
        {
            if (disposed || busy || String.IsNullOrWhiteSpace(prompt.Text)) return;
            var text = prompt.Text.Trim();
            var operation = ++operationVersion;
            CancellationTokenSource pendingStart = null;
            try
            {
                if (binding == null) throw new InvalidOperationException("Откройте книгу и нажмите «Привязать».");
                binding.Validate(); // Must remain on the Excel/WinForms UI thread.
                if (!acquireTurn()) throw new InvalidOperationException("В другом окне Excel уже выполняется запрос Aperture2. Дождитесь его завершения или остановите его.");
                SetBusy(true);
                pendingStart = new CancellationTokenSource();
                startupCancellation = pendingStart;
                if (session == null || !session.IsRunning)
                {
                    await DisposeSessionAsync();
                    if (!IsCurrentOperation(operation)) return;
                    pendingStart.Token.ThrowIfCancellationRequested();
                    SetStatus("Запускаю Pi и подключаю Excel-инструменты…");
                    var started = await PiSession.StartAsync(settings.ToOptions(), binding.Target, pendingStart.Token);
                    if (!IsCurrentOperation(operation) || pendingStart.IsCancellationRequested)
                    {
                        started.Dispose();
                        pendingStart.Token.ThrowIfCancellationRequested();
                        return;
                    }
                    session = started;
                    session.EventReceived += SessionEvent;
                    session.StandardError += SessionDiagnostic;
                    Diagnostics.Write("pi_session_started");
                }
                if (!IsCurrentOperation(operation)) return;
                pendingStart.Token.ThrowIfCancellationRequested();
                binding.Validate();
                AppendMessage("Вы", text);
                prompt.Clear();
                SetStatus("Pi работает…");
                await session.SendPromptAsync(text);
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentOperation(operation)) { SetBusy(false); SetStatus("Запуск отменён · запрос не отправлен"); }
            }
            catch (Exception ex)
            {
                if (IsCurrentOperation(operation))
                {
                    AppendMessage("Ошибка", ex.Message);
                    if (ex is TimeoutException && session != null && session.IsRunning)
                    {
                        SetStatus("Статус запроса неизвестен · остановите Pi перед повтором", true);
                        stop.Enabled = true;
                    }
                    else { SetStatus("Не удалось выполнить запрос", true); SetBusy(false); }
                }
                Diagnostics.Write("request_failed", ex.GetType().Name);
            }
            finally
            {
                if (ReferenceEquals(startupCancellation, pendingStart)) startupCancellation = null;
                pendingStart?.Dispose();
            }
        }

        private async Task AbortAsync()
        {
            if (disposed || transitioning || !busy) return;
            var operation = operationVersion;
            // Stop can arrive while StartAsync still owns its process locally. Cancel before
            // looking at session, otherwise that process could later submit the pending prompt.
            startupCancellation?.Cancel();
            stop.Enabled = false;
            SetStatus("Останавливаю ответ Pi…");
            var current = session;
            if (current == null) return;
            try
            {
                await current.AbortAsync();
                if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                var state = await current.GetStateAsync();
                if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                if ((bool?)state["data"]?["isStreaming"] == false) { EndAssistant(); SetBusy(false); SetStatus("Ответ остановлен"); }
            }
            catch (Exception ex)
            {
                if (!IsCurrentOperation(operation) || !ReferenceEquals(current, session)) return;
                AppendMessage("Ошибка остановки", ex.Message);
                if (!current.IsRunning) SetBusy(false);
                else { stop.Enabled = true; SetStatus("Остановка не подтверждена · дождитесь завершения", true); }
            }
        }

        private async Task RebindAsync()
        {
            if (disposed || busy) return;
            var operation = BeginTransition();
            try
            {
                await DisposeSessionAsync();
                if (!IsCurrentOperation(operation)) return;
                TryCaptureWorkbook();
                AppendMessage("Система", "Новая привязка начинает отдельную сессию Pi.");
            }
            catch (Exception ex) { if (IsCurrentOperation(operation)) SetStatus(ex.Message, true); }
            finally { EndTransition(operation); }
        }

        private async Task ResetChatAsync()
        {
            if (disposed || busy) return;
            var operation = BeginTransition();
            try
            {
                await DisposeSessionAsync();
                if (!IsCurrentOperation(operation)) return;
                transcript.Clear(); assistantStreaming = false;
                AppendMessage("Aperture2", "Новый чат. История предыдущей сессии остаётся у Pi.");
                SetStatus("Готова к новому запросу");
            }
            catch (Exception ex) { if (IsCurrentOperation(operation)) SetStatus(ex.Message, true); }
            finally { EndTransition(operation); }
        }

        private bool IsCurrentOperation(int operation) { return !disposed && operation == operationVersion; }

        private int BeginTransition()
        {
            var operation = ++operationVersion;
            transitioning = true;
            startupCancellation?.Cancel();
            SetBusy(true);
            stop.Enabled = false;
            SetStatus("Закрываю предыдущую сессию Pi…");
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
            try { await previous.StopAsync(); }
            finally { previous.Dispose(); }
        }

        private void SessionDiagnostic(object sender, PiDiagnosticEventArgs args)
        {
            OnUi(() => { if (ReferenceEquals(sender, session)) SetStatus(args.Message, true); });
        }

        private void SessionEvent(object sender, PiEvent args)
        {
            OnUi(() =>
            {
                if (!ReferenceEquals(sender, session)) return;
                var json = args.Json;
                if (args.Type == "message_update")
                {
                    var update = json["assistantMessageEvent"];
                    if ((string)update?["type"] == "text_delta") AppendAssistant((string)update["delta"]);
                }
                else if (args.Type == "message_end")
                {
                    var message = json["message"];
                    if ((string)message?["role"] == "assistant")
                    {
                        if (!assistantStreaming)
                        {
                            var parts = message["content"] as JArray;
                            if (parts != null) foreach (var part in parts) if ((string)part["type"] == "text") AppendAssistant((string)part["text"]);
                        }
                        EndAssistant();
                        var error = (string)message["errorMessage"];
                        if (!String.IsNullOrWhiteSpace(error)) AppendMessage("Ошибка Pi", error);
                    }
                }
                else if (args.Type == "tool_execution_start")
                {
                    EndAssistant();
                    AppendMessage("Инструмент", (string)json["toolName"] ?? "Excel");
                    SetStatus("Выполняется инструмент…");
                }
                else if (args.Type == "tool_execution_end")
                {
                    if ((bool?)json["isError"] == true) AppendMessage("Инструмент", "Вернул ошибку; подробности доступны Pi для ответа.");
                    SetStatus("Pi обрабатывает результат…");
                }
                else if (args.Type == "agent_settled")
                {
                    EndAssistant(); SetBusy(false); SetStatus("Готово");
                }
                else if (args.Type == "process_exit" || args.Type == "error")
                {
                    EndAssistant(); SetBusy(false); SetStatus("Pi остановлен или вернул ошибку", true);
                }
            });
        }

        private void ShowSettings(object sender, EventArgs args)
        {
            using (var dialog = new SettingsDialog(settings))
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { settings = dialog.Settings; SetStatus("Настройки сохранены · начните новый чат"); }
        }

        private void AppendMessage(string who, string text)
        {
            EndAssistant();
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionColor = who == "Вы" ? Color.FromArgb(25, 91, 142) : Color.FromArgb(64, 70, 77);
            transcript.SelectionFont = new Font(transcript.Font, FontStyle.Bold);
            transcript.AppendText(who + Environment.NewLine);
            transcript.SelectionFont = transcript.Font;
            transcript.SelectionColor = Color.FromArgb(30, 36, 43);
            transcript.AppendText(text + Environment.NewLine + Environment.NewLine);
            transcript.ScrollToCaret();
        }

        private void AppendAssistant(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            if (!assistantStreaming)
            {
                transcript.SelectionStart = transcript.TextLength;
                transcript.SelectionColor = Color.FromArgb(24, 95, 73);
                transcript.SelectionFont = new Font(transcript.Font, FontStyle.Bold);
                transcript.AppendText("Pi" + Environment.NewLine);
                transcript.SelectionColor = Color.FromArgb(30, 36, 43);
                transcript.SelectionFont = transcript.Font;
                assistantStreaming = true;
            }
            transcript.AppendText(text); transcript.ScrollToCaret();
        }

        private void EndAssistant()
        {
            if (!assistantStreaming) return;
            transcript.AppendText(Environment.NewLine + Environment.NewLine);
            assistantStreaming = false;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            if (!value) releaseTurn();
            send.Enabled = bind.Enabled = newChat.Enabled = settingsButton.Enabled = !value;
            stop.Enabled = value;
        }

        private void SetStatus(string text, bool error = false)
        {
            status.Text = text; status.ForeColor = error ? Color.FromArgb(165, 48, 48) : Color.FromArgb(70, 84, 96);
        }

        private void OnUi(Action action)
        {
            if (disposed || IsDisposed || !IsHandleCreated) return;
            try { if (InvokeRequired) BeginInvoke(action); else action(); }
            catch (InvalidOperationException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                ++operationVersion;
                startupCancellation?.Cancel();
                releaseTurn();
                if (session != null)
                {
                    session.EventReceived -= SessionEvent; session.StandardError -= SessionDiagnostic;
                    session.Dispose(); session = null;
                }
                binding = null;
                captureWorkbook = null;
            }
            base.Dispose(disposing);
        }
    }
}
