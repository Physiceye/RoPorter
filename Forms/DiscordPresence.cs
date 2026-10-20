using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Rbx2Source
{
    // Discord Rich Presence over Discord's local IPC pipe (\\.\pipe\discord-ipc-0 to 9): a handshake with the application
    // ID, then SET_ACTIVITY frames. Each frame is a little-endian opcode and length followed by UTF-8 JSON. Discord not
    // running (or closing) is not an error: updates are skipped and the next one tries to connect again.
    public sealed class DiscordPresence : IDisposable
    {
        // The Discord application's ID (Discord Developer Portal > Applications > RoPorter > Application ID). Its name is
        // what Discord shows ("Playing RoPorter"); "logo" is the art asset uploaded under Rich Presence > Art Assets.
        public const string ApplicationId = "1561193322758930573";
        private const string LargeImage = "logo";
        // The small corner image ("part_icon" art asset), whose hover text is the app version (e.g. "v1.2.0").
        private const string SmallImage = "part_icon";
        private static readonly string Version = "v" + typeof(DiscordPresence).Assembly.GetName().Version.ToString(3);

        private const int Handshake = 0, Frame = 1;
        private readonly long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream pipe;
        private bool disposed;

        public static bool Configured => ApplicationId.Length > 0;

        // Shows "details" and "state" under RoPorter, with the time since RoPorter started. Never throws.
        public Task SetAsync(string details, string state)
        {
            var activity = new JObject
            {
                ["timestamps"] = new JObject { ["start"] = started },
                ["assets"] = new JObject
                {
                    ["large_image"] = LargeImage, ["large_text"] = "RoPorter",
                    ["small_image"] = SmallImage, ["small_text"] = Version
                }
            };
            // Discord rejects the whole activity if a text field is null, so empty lines are left out.
            if (Limit(details) is string top)
                activity["details"] = top;
            if (Limit(state) is string bottom)
                activity["state"] = bottom;
            // Off the UI thread: connecting waits on each of Discord's pipes in turn.
            return Task.Run(() => SendAsync(activity));
        }

        // Removes the activity (when Rich Presence is turned off).
        public Task ClearAsync() => Task.Run(() => SendAsync(null));

        // After Discord can't be reached, connecting is tried again only after a while, not on every update.
        private DateTime nextConnect = DateTime.MinValue;
        private static readonly TimeSpan RetryConnect = TimeSpan.FromSeconds(15);

        // Discord rejects empty strings and strings over 128 characters.
        private static string Limit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            return text.Length < 2 ? text + " " : text.Length > 128 ? text.Substring(0, 127) + "…" : text;
        }

        private async Task SendAsync(JObject activity)
        {
            if (!Configured || disposed)
                return;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (pipe == null || !pipe.IsConnected)
                {
                    if (DateTime.UtcNow < nextConnect)
                        return;
                    await ConnectAsync().ConfigureAwait(false);
                }
                if (pipe == null)
                {
                    nextConnect = DateTime.UtcNow + RetryConnect;
                    return;
                }
                var command = new JObject
                {
                    ["cmd"] = "SET_ACTIVITY", ["nonce"] = Guid.NewGuid().ToString(),
                    ["args"] = new JObject { ["pid"] = Process.GetCurrentProcess().Id, ["activity"] = activity }
                };
                await WriteAsync(Frame, command).ConfigureAwait(false);
                await ReadAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException ||
                error is TimeoutException || error is OperationCanceledException || error is UnauthorizedAccessException ||
                error is Newtonsoft.Json.JsonException)
            {
                // A reply that never came (the read timed out) or a closed pipe: start over on the next update.
                Disconnect();
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task ConnectAsync()
        {
            Disconnect();
            for (int i = 0; i < 10; i++)
            {
                var candidate = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    candidate.Connect(100);
                }
                catch (Exception error) when (error is TimeoutException || error is IOException)
                {
                    candidate.Dispose();
                    continue;
                }
                pipe = candidate;
                await WriteAsync(Handshake, new JObject { ["v"] = 1, ["client_id"] = ApplicationId }).ConfigureAwait(false);
                await ReadAsync().ConfigureAwait(false);
                return;
            }
        }

        private async Task WriteAsync(int opcode, JObject payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(opcode).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            await pipe.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
        }

        // Reads (and discards) Discord's reply, so its pipe buffer never fills. A close frame (opcode 2) ends the connection.
        private async Task ReadAsync()
        {
            var header = new byte[8];
            await ReadExactlyAsync(header).ConfigureAwait(false);
            int opcode = BitConverter.ToInt32(header, 0), length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 1 << 20)
                throw new IOException("Discord sent an invalid frame.");
            await ReadExactlyAsync(new byte[length]).ConfigureAwait(false);
            if (opcode == 2)
                throw new IOException("Discord closed the connection.");
        }

        private async Task ReadExactlyAsync(byte[] buffer)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                for (int read = 0; read < buffer.Length;)
                {
                    int count = await pipe.ReadAsync(buffer, read, buffer.Length - read, timeout.Token).ConfigureAwait(false);
                    if (count == 0)
                        throw new IOException("Discord closed the connection.");
                    read += count;
                }
        }

        private void Disconnect()
        {
            pipe?.Dispose();
            pipe = null;
        }

        public void Dispose()
        {
            disposed = true;
            Disconnect();
        }
    }
}
