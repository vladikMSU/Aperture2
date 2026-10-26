using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aperture.Runtime
{
    /// <summary>One Pi subprocess per bound chat. Events arrive on a worker thread.</summary>
    public sealed class PiSession : IDisposable
    {
        private readonly Process process;
        private readonly ChildProcessJob job;
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        private readonly ConcurrentQueue<string> diagnostics = new ConcurrentQueue<string>();
        private readonly TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int timeoutSeconds;
        private StreamWriter input;
        private Task outputTask, errorTask;
        private int disposed;
        private int stopping;
        private static readonly HashSet<string> AllowedCommands = new HashSet<string>(StringComparer.Ordinal)
        { "prompt", "abort", "get_state", "get_available_models", "get_commands", "get_messages", "get_entries", "get_last_assistant_text", "get_session_stats" };

        public event EventHandler<PiEvent> EventReceived;
        public event EventHandler<PiDiagnosticEventArgs> StandardError;
        public string SessionDirectory { get; }
        /// <summary>Pi's JSONL history of this chat, reported by get_state. Pi creates the file with the first message.</summary>
        public string SessionFile { get; private set; }
        /// <summary>SHA-256 of the system prompt shared by all chats, without the per-chat binding line.</summary>
        public static string SystemPromptSha256 => SessionConfiguration.SystemPromptSha256;
        public int ProcessId => process.Id;
        public bool IsRunning => Volatile.Read(ref disposed) == 0 && !process.HasExited;

        private PiSession(SessionConfiguration config, int timeoutSeconds)
        {
            this.timeoutSeconds = timeoutSeconds;
            SessionDirectory = config.Directory;
            job = new ChildProcessJob();
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = config.Node,
                    Arguments = config.Arguments,
                    WorkingDirectory = config.Directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.EnvironmentVariables["PI_SKIP_VERSION_CHECK"] = "1";
            process.StartInfo.EnvironmentVariables["PI_TELEMETRY"] = "0";
            foreach (var key in config.ProviderEnvironment) process.StartInfo.EnvironmentVariables[key.Key] = key.Value;
            process.Exited += (sender, args) => exited.TrySetResult(true);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Node did not start.");
                job.Add(process);
                input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                outputTask = ReadOutputAsync();
                errorTask = ReadErrorAsync();
            }
            catch
            {
                job.Dispose();
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                process.Dispose();
                throw;
            }
        }

        public static async Task<PiSession> StartAsync(PiSessionOptions options, ExcelTarget target, CancellationToken cancellationToken = default)
        {
            // Configuration IO/process setup also run off the Excel UI thread.
            var config = await Task.Run(() => SessionConfiguration.Create(options, target), cancellationToken).ConfigureAwait(false);
            return await StartPreparedAsync(config, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<PiSession> StartPreparedAsync(SessionConfiguration config, int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            var session = await Task.Run(() => new PiSession(config, timeoutSeconds), cancellationToken).ConfigureAwait(false);
            try
            {
                await session.input.WriteAsync("\n").ConfigureAwait(false); // release process gate only after job assignment
                var state = await session.GetStateAsync(cancellationToken).ConfigureAwait(false);
                session.SessionFile = (string)state["data"]?["sessionFile"];
                return session;
            }
            catch (Exception error)
            {
                string details = string.Join(Environment.NewLine, session.diagnostics);
                session.Dispose();
                if (error is OperationCanceledException) throw;
                throw new InvalidOperationException("Pi RPC failed to start. " + error.Message + (details.Length == 0 ? "" : Environment.NewLine + details), error);
            }
        }

        public Task<JObject> SendPromptAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Prompt is empty.", nameof(message));
            return SendCommandAsync(new JObject { ["type"] = "prompt", ["message"] = message }, cancellationToken);
        }
        public Task<JObject> AbortAsync(CancellationToken cancellationToken = default) => SendCommandAsync(new JObject { ["type"] = "abort" }, cancellationToken);
        public Task<JObject> GetStateAsync(CancellationToken cancellationToken = default) => SendCommandAsync(new JObject { ["type"] = "get_state" }, cancellationToken);

        /// <summary>Returns the complete correlated response. Prompt acceptance is not run completion; wait for agent_settled.</summary>
        public async Task<JObject> SendCommandAsync(JObject command, CancellationToken cancellationToken = default)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!AllowedCommands.Contains((string)command["type"] ?? "")) throw new ArgumentException("This RPC command is not exposed by Aperture.");
            if (!IsRunning) throw new InvalidOperationException("Pi is not running.");
            cancellationToken.ThrowIfCancellationRequested();
            string id = Guid.NewGuid().ToString("N");
            var copy = (JObject)command.DeepClone();
            copy["id"] = id;
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                await WriteRecordAsync(copy, cancellationToken).ConfigureAwait(false);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task delay = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), deadline.Token);
                    Task winner = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
                    if (winner != completion.Task)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("Pi RPC response timed out. This does not prove that an in-flight operation was cancelled; do not repeat a write blindly.");
                    }
                    deadline.Cancel();
                    JObject response = await completion.Task.ConfigureAwait(false);
                    if ((bool?)response["success"] != true) throw new InvalidOperationException((string)response["error"] ?? "Pi rejected the command.");
                    return response;
                }
            }
            finally { pending.TryRemove(id, out _); }
        }

        private async Task WriteRecordAsync(JObject record, CancellationToken cancellationToken)
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsRunning) throw new InvalidOperationException("Pi is not running.");
                await input.WriteLineAsync(record.ToString(Formatting.None)).ConfigureAwait(false);
            }
            finally { writeLock.Release(); }
        }

        private async Task ReadOutputAsync()
        {
            Exception failure = null;
            try
            {
                string line;
                // .NET's reader recognizes CR/LF only, preserving U+2028/U+2029 inside JSON strings.
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0) continue;
                    JObject record;
                    try { record = JObject.Parse(line); }
                    catch (JsonException ex) { throw new IOException("Pi emitted a non-JSON protocol record.", ex); }
                    string type = (string)record["type"];
                    if (type == "response" && (string)record["id"] is string id && pending.TryGetValue(id, out var completion))
                        completion.TrySetResult(record);
                    else
                    {
                        if (type == "extension_ui_request") await CancelUnsupportedDialogAsync(record).ConfigureAwait(false);
                        NotifyEvent(record);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { failure = ex; }
            finally
            {
                FailPending(failure ?? new IOException("Pi exited or closed its RPC stream."));
                if (Volatile.Read(ref disposed) == 0)
                {
                    if (Volatile.Read(ref stopping) == 0)
                    {
                        NotifyDiagnostic(failure?.Message ?? "Pi exited. Start a new chat to reconnect.");
                        NotifyEvent(new JObject { ["type"] = "process_exit" });
                    }
                    job.Dispose();
                }
            }
        }

        private async Task CancelUnsupportedDialogAsync(JObject request)
        {
            string method = (string)request["method"];
            if (method == "confirm" || method == "select" || method == "input" || method == "editor")
            {
                await WriteRecordAsync(new JObject { ["type"] = "extension_ui_response", ["id"] = request["id"], ["cancelled"] = true }, CancellationToken.None).ConfigureAwait(false);
                NotifyDiagnostic("Pi requested an unsupported dialog (" + method + "); it was cancelled.");
            }
        }

        private async Task ReadErrorAsync()
        {
            try
            {
                string line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) NotifyDiagnostic(line);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        }

        private void NotifyEvent(JObject record)
        {
            try { EventReceived?.Invoke(this, new PiEvent(record)); }
            catch (Exception ex) { NotifyDiagnostic("Event handler failed: " + ex.Message); }
        }
        private void NotifyDiagnostic(string message)
        {
            diagnostics.Enqueue(message);
            while (diagnostics.Count > 8) diagnostics.TryDequeue(out _);
            try { StandardError?.Invoke(this, new PiDiagnosticEventArgs(message)); } catch { }
        }
        private void FailPending(Exception error)
        {
            foreach (var item in pending) item.Value.TrySetException(error);
        }

        public async Task StopAsync()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Interlocked.Exchange(ref stopping, 1);
            await writeLock.WaitAsync().ConfigureAwait(false);
            try { input.Close(); } catch (IOException) { } finally { writeLock.Release(); }
            await Task.WhenAny(exited.Task, Task.Delay(3000)).ConfigureAwait(false);
            Dispose();
        }

        /// <summary>Immediate local cleanup, safe during Excel shutdown; never waits on Excel's UI thread.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            FailPending(new ObjectDisposedException(nameof(PiSession)));
            job.Dispose(); // kills only the gated Node process and its descendants, never Excel
            try { input?.Dispose(); } catch (IOException) { }
            // Readers still own Process streams; release the handle once they have drained.
            Task.WhenAll(outputTask ?? Task.CompletedTask, errorTask ?? Task.CompletedTask)
                .ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
    }
}
