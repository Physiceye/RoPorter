using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Rbx2Source
{
    // Discord Rich Presence over Discord's local IPC channel: the pipe \\.\pipe\discord-ipc-0 to 9 on Windows, the socket
    // discord-ipc-0 to 9 in the runtime or temporary folder on Linux and macOS (also inside Flatpak and Snap installs). A
    // handshake with the application ID, then SET_ACTIVITY frames; each frame is a little-endian opcode and length followed by
    // UTF-8 JSON. Discord not running (or closing) is not an error: updates are skipped and the next one tries to connect again.
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
        private Stream channel;
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
            // Off the UI thread: connecting waits on each of Discord's channels in turn.
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
                if (channel == null)
                {
                    if (DateTime.UtcNow < nextConnect)
                        return;
                    await ConnectAsync().ConfigureAwait(false);
                }
                if (channel == null)
                {
                    nextConnect = DateTime.UtcNow + RetryConnect;
                    return;
                }
                var command = new JObject
                {
                    ["cmd"] = "SET_ACTIVITY", ["nonce"] = Guid.NewGuid().ToString(),
                    ["args"] = new JObject { ["pid"] = Environment.ProcessId, ["activity"] = activity }
                };
                await WriteAsync(Frame, command).ConfigureAwait(false);
                await ReadAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException ||
                error is TimeoutException || error is OperationCanceledException || error is UnauthorizedAccessException ||
                error is SocketException || error is Newtonsoft.Json.JsonException)
            {
                // A reply that never came (the read timed out) or a closed channel: start over on the next update.
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
            for (int i = 0; i < 10 && channel == null; i++)
                channel = OperatingSystem.IsWindows() ? ConnectPipe(i) : ConnectSocket(i);
            if (channel == null)
                return;
            await WriteAsync(Handshake, new JObject { ["v"] = 1, ["client_id"] = ApplicationId }).ConfigureAwait(false);
            await ReadAsync().ConfigureAwait(false);
        }

        private static Stream ConnectPipe(int index)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + index, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(100);
                return pipe;
            }
            catch (Exception error) when (error is TimeoutException || error is IOException)
            {
                pipe.Dispose();
                return null;
            }
        }

        // Discord's socket on Linux and macOS: in the runtime folder (or the temporary one), directly or inside a Flatpak or
        // Snap install's folder.
        private static Stream ConnectSocket(int index)
        {
            foreach (string variable in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" })
            {
                string folder = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(folder))
                    continue;
                foreach (string sub in new[] { "", "app/com.discordapp.Discord", "snap.discord", ".flatpak/com.discordapp.Discord/xdg-run" })
                {
                    string path = Path.Combine(folder, sub, "discord-ipc-" + index);
                    if (Connect(path) is Stream stream)
                        return stream;
                }
            }
            return Connect(Path.Combine("/tmp", "discord-ipc-" + index));
        }

        private static Stream Connect(string path)
        {
            if (!File.Exists(path))
                return null;
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(path));
                return new NetworkStream(socket, true);
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
        }

        private async Task WriteAsync(int opcode, JObject payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(opcode).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            await channel.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            await channel.FlushAsync().ConfigureAwait(false);
        }

        // Reads (and discards) Discord's reply, so its buffer never fills. A close frame (opcode 2) ends the connection.
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
                    int count = await channel.ReadAsync(buffer, read, buffer.Length - read, timeout.Token).ConfigureAwait(false);
                    if (count == 0)
                        throw new IOException("Discord closed the connection.");
                    read += count;
                }
        }

        private void Disconnect()
        {
            channel?.Dispose();
            channel = null;
        }

        public void Dispose()
        {
            disposed = true;
            Disconnect();
        }
    }
}
