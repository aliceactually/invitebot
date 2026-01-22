using System.Text;
using Discord;
using Discord.WebSocket;

namespace InviteBot {

    // Centralised console logger. Every line is prefixed with a UTC timestamp, a severity tag,
    // and an optional source so logs from a deployed instance can be grepped and correlated.
    // Exceptions are always formatted with their type, message, full stack trace, and the
    // complete inner-exception chain - the previous "{x.Message}" pattern was hiding far too
    // much information when something failed in production.
    internal static class Log {

        private enum Level { Debug, Info, Warn, Error }

        // Mirrors the global config debug flag; set once in Main after config load.
        public static bool DebugEnabled { get; set; }

        public static void Info(string source, string message) => Write(Level.Info, source, message, null);
        public static void Warn(string source, string message, Exception? ex = null) => Write(Level.Warn, source, message, ex);
        public static void Error(string source, string message, Exception? ex = null) => Write(Level.Error, source, message, ex);

        public static void Debug(string source, string message) {
            if (!DebugEnabled) { return; }
            Write(Level.Debug, source, message, null);
        }

        // Routes Discord.Net's own log stream through our formatter so timestamps and severity
        // tags are uniform across the bot. Verbose/Debug from the gateway are dropped unless our
        // debug flag is on, otherwise the console is overwhelmed with heartbeats.
        public static Task FromDiscord(LogMessage msg) {
            // Demote routine gateway lifecycle exceptions: Discord rotates sessions every few
            // hours and idle TCP connections get dropped by intermediate networks. Discord.Net
            // recovers automatically; treating these as warnings with full stack traces makes
            // healthy operation look alarming. Reported as a single info line, no stack.
            if (msg.Exception is not null && IsRoutineGatewayDisconnect(msg.Exception)) {
                string reason = msg.Exception is GatewayReconnectException
                    ? "Discord requested reconnect"
                    : "Gateway connection dropped";
                Write(Level.Info, $"discord/{msg.Source}", $"{reason}; reconnecting", null);
                return Task.CompletedTask;
            }

            switch (msg.Severity) {
                case LogSeverity.Critical:
                case LogSeverity.Error:
                    Write(Level.Error, $"discord/{msg.Source}", msg.Message ?? string.Empty, msg.Exception);
                    break;
                case LogSeverity.Warning:
                    Write(Level.Warn, $"discord/{msg.Source}", msg.Message ?? string.Empty, msg.Exception);
                    break;
                case LogSeverity.Info:
                    Write(Level.Info, $"discord/{msg.Source}", msg.Message ?? string.Empty, msg.Exception);
                    break;
                default:
                    if (DebugEnabled) {
                        Write(Level.Debug, $"discord/{msg.Source}", msg.Message ?? string.Empty, msg.Exception);
                    }
                    break;
            }
            return Task.CompletedTask;
        }

        private static bool IsRoutineGatewayDisconnect(Exception ex) {
            // Server-initiated rotation
            if (ex is GatewayReconnectException) { return true; }
            // Idle drop / mid-stream close - the inner exception is the one with the close-handshake reason
            Exception? current = ex;
            while (current is not null) {
                if (current is System.Net.WebSockets.WebSocketException) { return true; }
                current = current.InnerException;
            }
            return false;
        }

        private static void Write(Level level, string source, string message, Exception? ex) {
            StringBuilder sb = new();
            sb.Append('[').Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append("Z] [")
              .Append(level.ToString().ToUpperInvariant()).Append("] [").Append(source).Append("] ")
              .Append(message);

            if (ex is not null) {
                sb.AppendLine();
                AppendException(sb, ex, prefix: "  ");
            }

            // Errors and warnings go to stderr so they can be redirected separately by the host.
            TextWriter writer = level == Level.Error || level == Level.Warn ? Console.Error : Console.Out;
            writer.WriteLine(sb.ToString());
        }

        private static void AppendException(StringBuilder sb, Exception ex, string prefix) {
            Exception? current = ex;
            int depth = 0;
            while (current is not null) {
                if (depth == 0) {
                    sb.Append(prefix).Append("-> ");
                } else {
                    sb.Append(prefix).Append("-> [inner ").Append(depth).Append("] ");
                }
                sb.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
                if (!string.IsNullOrEmpty(current.StackTrace)) {
                    foreach (string line in current.StackTrace.Split('\n')) {
                        sb.Append(prefix).Append("   ").AppendLine(line.TrimEnd('\r'));
                    }
                }
                current = current.InnerException;
                depth++;
            }
        }
    }
}
