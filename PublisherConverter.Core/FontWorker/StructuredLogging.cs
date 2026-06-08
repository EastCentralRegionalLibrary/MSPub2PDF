using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PublisherConverter.Core.FontWorker
{
    public enum StructuredLogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// Minimal structured logging seam shared by the main process and the
    /// elevated worker process. Each entry is an event name plus a flat bag of
    /// fields and an optional correlation id, so logs stay machine-readable and
    /// can be correlated end-to-end across the process boundary.
    ///
    /// Implementations must be safe to call from multiple threads.
    /// </summary>
    public interface IStructuredLogger
    {
        void Log(
            StructuredLogLevel level,
            string eventName,
            string? correlationId = null,
            IReadOnlyDictionary<string, object?>? data = null);
    }

    /// <summary>Discards every entry. Default when no logging is wired up.</summary>
    public sealed class NullStructuredLogger : IStructuredLogger
    {
        public static readonly NullStructuredLogger Instance = new NullStructuredLogger();

        private NullStructuredLogger()
        {
        }

        public void Log(StructuredLogLevel level, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
        {
        }
    }

    /// <summary>
    /// Captures entries in memory. Used by tests to assert on the structured
    /// log without touching the filesystem or a real console.
    /// </summary>
    public sealed class InMemoryStructuredLogger : IStructuredLogger
    {
        public sealed class Entry
        {
            public StructuredLogLevel Level { get; init; }
            public string EventName { get; init; } = string.Empty;
            public string? CorrelationId { get; init; }
            public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly object _lock = new object();

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_lock) { return new List<Entry>(_entries); } }
        }

        public void Log(StructuredLogLevel level, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
        {
            var entry = new Entry
            {
                Level = level,
                EventName = eventName,
                CorrelationId = correlationId,
                Data = data ?? new Dictionary<string, object?>(),
            };
            lock (_lock) { _entries.Add(entry); }
        }
    }

    /// <summary>
    /// Writes one JSON object per line (JSON Lines / NDJSON) to a
    /// <see cref="TextWriter"/>. Suitable for a worker log file or stderr and
    /// trivially parseable by log shippers / troubleshooting scripts.
    /// </summary>
    public sealed class JsonLinesStructuredLogger : IStructuredLogger, IDisposable
    {
        private readonly TextWriter _writer;
        private readonly bool _ownsWriter;
        private readonly string _source;
        private readonly object _lock = new object();

        /// <param name="writer">Destination writer (file, console, …).</param>
        /// <param name="source">Logical source name stamped on every line (e.g. "main" or "font-worker").</param>
        /// <param name="ownsWriter">When true, the writer is disposed with this logger.</param>
        public JsonLinesStructuredLogger(TextWriter writer, string source, bool ownsWriter = false)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _source = source ?? string.Empty;
            _ownsWriter = ownsWriter;
        }

        public void Log(StructuredLogLevel level, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
        {
            var record = new Dictionary<string, object?>
            {
                ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = level.ToString(),
                ["source"] = _source,
                ["event"] = eventName,
            };
            if (!string.IsNullOrEmpty(correlationId)) record["correlationId"] = correlationId;
            if (data != null)
            {
                foreach (var kv in data)
                {
                    // Never let a field clobber a reserved key.
                    if (!record.ContainsKey(kv.Key)) record[kv.Key] = kv.Value;
                }
            }

            string line;
            try
            {
                line = JsonSerializer.Serialize(record);
            }
            catch
            {
                // Serialization must never take down the caller; fall back to a
                // minimal, always-serializable line.
                line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["level"] = level.ToString(),
                    ["source"] = _source,
                    ["event"] = eventName,
                    ["correlationId"] = correlationId,
                });
            }

            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
                catch
                {
                    // Logging is best-effort; a broken sink must not fail the operation.
                }
            }
        }

        public void Dispose()
        {
            if (_ownsWriter)
            {
                try { _writer.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Fans an entry out to several loggers (e.g. file + console).</summary>
    public sealed class CompositeStructuredLogger : IStructuredLogger
    {
        private readonly IReadOnlyList<IStructuredLogger> _loggers;

        public CompositeStructuredLogger(params IStructuredLogger[] loggers)
        {
            _loggers = loggers ?? Array.Empty<IStructuredLogger>();
        }

        public void Log(StructuredLogLevel level, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
        {
            foreach (var logger in _loggers)
            {
                try { logger.Log(level, eventName, correlationId, data); } catch { }
            }
        }
    }

    /// <summary>Convenience helpers for building the field bag fluently.</summary>
    public static class StructuredLoggerExtensions
    {
        public static void Info(this IStructuredLogger logger, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
            => logger.Log(StructuredLogLevel.Info, eventName, correlationId, data);

        public static void Warn(this IStructuredLogger logger, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
            => logger.Log(StructuredLogLevel.Warning, eventName, correlationId, data);

        public static void Error(this IStructuredLogger logger, string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? data = null)
            => logger.Log(StructuredLogLevel.Error, eventName, correlationId, data);
    }
}
