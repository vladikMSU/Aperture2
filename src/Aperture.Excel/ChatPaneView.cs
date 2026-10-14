using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aperture.Excel
{
    internal sealed class ChatPaneCommandEventArgs : EventArgs
    {
        internal string Command { get; private set; }
        internal string Text { get; private set; }
        internal ChatPaneCommandEventArgs(string command, string text) { Command = command; Text = text; }
    }

    /// <summary>Local HTML view. Only chat commands cross this bridge; credentials and COM objects never do.</summary>
    internal sealed class ChatPaneView : UserControl
    {
        private const string AppHost = "aperture.invalid";
        private const string AppUrl = "https://aperture.invalid/chat.html";
        private readonly WebView2 browser;
        private readonly Label loading;
        private readonly Queue<JObject> pending = new Queue<JObject>();
        private readonly object pendingLock = new object();
        private bool initializing;
        private bool ready;
        private bool disposed;
        private string initializationError;

        internal bool IsReady { get { return ready; } }
        internal string InitializationError { get { return initializationError; } }

        internal event EventHandler<ChatPaneCommandEventArgs> Command;

        internal ChatPaneView()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(29, 31, 35);
            browser = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = BackColor,
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aperture2", "webview2")
                }
            };
            loading = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Открываю Aperture…",
                ForeColor = Color.FromArgb(170, 176, 186),
                BackColor = BackColor,
                Font = new Font("Segoe UI", 10F),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(22)
            };
            Controls.Add(browser);
            Controls.Add(loading);
            browser.CoreWebView2InitializationCompleted += BrowserInitialized;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!initializing) PostUi(InitializeBrowser);
        }

        private void InitializeBrowser()
        {
            if (disposed || initializing) return;
            initializing = true;
            try
            {
                // Excel can invoke add-in callbacks with no SynchronizationContext. All
                // WebView calls stay in UI callbacks; no await continuation touches COM.
                var initialization = browser.EnsureCoreWebView2Async();
                initialization.ContinueWith(t =>
                {
                    var error = t.Exception.GetBaseException(); // Observe initialization failure.
                    PostUi(() => ShowFailure(error));
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { ShowFailure(ex); }
        }

        private void BrowserInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (disposed) return;
            if (!e.IsSuccess) { ShowFailure(e.InitializationException); return; }
            try
            {
                var assets = Path.Combine(Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath), "Web");
                if (!File.Exists(Path.Combine(assets, "chat.html"))) throw new FileNotFoundException("Не найдены файлы интерфейса Aperture (Web/chat.html). Пересоберите надстройку.");
                var core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDevToolsEnabled = false;
                // Reload would discard DOM state while the owning Pi session keeps
                // running. Keep browser shortcuts off; copy/paste/editing still work.
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.SetVirtualHostNameToFolderMapping(AppHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (s, args) => { if (!IsAppDocument(args.Uri)) args.Cancel = true; };
                core.NewWindowRequested += (s, args) => { args.Handled = true; };
                core.DownloadStarting += (s, args) => { args.Cancel = true; };
                core.PermissionRequested += (s, args) => { args.State = CoreWebView2PermissionState.Deny; };
                core.WebMessageReceived += BrowserMessage;
                core.ProcessFailed += (s, args) => ShowFailure(new InvalidOperationException("Процесс интерфейса остановлен. Перезапустите Excel, чтобы восстановить панель Aperture."));
                core.NavigationCompleted += (s, args) =>
                {
                    if (!args.IsSuccess) ShowFailure(new InvalidOperationException("Не удалось открыть интерфейс: " + args.WebErrorStatus));
                };
                core.Navigate(AppUrl);
            }
            catch (Exception ex) { ShowFailure(ex); }
        }

        private static bool IsAppDocument(string source)
        {
            Uri uri;
            return Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                uri.Host == AppHost && uri.IsDefaultPort && uri.AbsolutePath == "/chat.html";
        }

        private void BrowserMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (disposed || !IsAppDocument(e.Source)) return;
            JObject message;
            try { message = JObject.Parse(e.WebMessageAsJson); }
            catch (JsonException) { return; }
            var command = (string)message["command"];
            if (command == "ready")
            {
                ready = true;
                initializationError = null;
                loading.Visible = false;
                FlushPending();
                return;
            }
            if (command != "send" && command != "stop" && command != "rebind" && command != "new_chat" && command != "settings") return;
            var text = (string)message["text"] ?? String.Empty;
            if (text.Length > 100000) return;
            // Settings can open a modal dialog. Defer beyond the WebView event to avoid
            // a nested message loop inside WebView2's COM callback.
            PostUi(() => Command?.Invoke(this, new ChatPaneCommandEventArgs(command, text)));
        }

        internal void SetBinding(string name) { Enqueue(new JObject { ["type"] = "binding", ["name"] = name }); }
        internal void SetStatus(string text, bool error) { Enqueue(new JObject { ["type"] = "status", ["text"] = text, ["error"] = error }); }
        internal void SetBusy(bool busy, bool canStop = true) { Enqueue(new JObject { ["type"] = "busy", ["busy"] = busy, ["canStop"] = canStop }); }
        internal void AppendMessage(string who, string text) { Enqueue(new JObject { ["type"] = "message", ["who"] = who, ["text"] = text }); }
        internal void AppendAssistant(string text) { Enqueue(new JObject { ["type"] = "assistant_delta", ["text"] = text }); }
        internal void EndAssistant() { Enqueue(new JObject { ["type"] = "assistant_end" }); }
        internal void ClearTranscript() { Enqueue(new JObject { ["type"] = "clear_transcript" }); }
        internal void ClearPrompt() { Enqueue(new JObject { ["type"] = "clear_prompt" }); }

        private void Enqueue(JObject message)
        {
            lock (pendingLock)
            {
                if (disposed) return;
                pending.Enqueue(message);
            }
            PostUi(FlushPending);
        }

        private void FlushPending()
        {
            if (disposed || !ready) return;
            try
            {
                lock (pendingLock)
                {
                    while (pending.Count != 0) browser.CoreWebView2.PostWebMessageAsJson(pending.Dequeue().ToString(Formatting.None));
                }
            }
            catch (Exception ex) { ShowFailure(ex); }
        }

        private void PostUi(Action action)
        {
            if (disposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!disposed) action(); })); }
            catch (InvalidOperationException) { /* Handle was destroyed during shutdown. */ }
        }

        private void ShowFailure(Exception error)
        {
            if (disposed) return;
            ready = false;
            initializationError = error?.Message ?? "Неизвестная ошибка WebView2.";
            loading.Text = "Не удалось открыть панель.\n\n" + (error?.Message ?? "Неизвестная ошибка WebView2.") + "\n\nПроверьте Microsoft Edge WebView2 Runtime и перезапустите Excel.";
            loading.Visible = true;
            loading.BringToFront();
            Diagnostics.Write("webview_failed", error?.GetType().Name);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (pendingLock) { disposed = true; pending.Clear(); }
                ready = false;
                Command = null;
                browser.CoreWebView2InitializationCompleted -= BrowserInitialized;
                browser.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
