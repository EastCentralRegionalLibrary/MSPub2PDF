using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    public class StructuredLoggingTests
    {
        [Fact]
        public void InMemoryLogger_CapturesLevelEventCorrelationAndData()
        {
            var logger = new InMemoryStructuredLogger();
            logger.Info("provision.start", "corr-1", new Dictionary<string, object?> { ["missing"] = 3 });

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(StructuredLogLevel.Info, entry.Level);
            Assert.Equal("provision.start", entry.EventName);
            Assert.Equal("corr-1", entry.CorrelationId);
            Assert.Equal(3, entry.Data["missing"]);
        }

        [Fact]
        public void JsonLinesLogger_WritesParseableJsonPerLine_WithReservedFields()
        {
            var sb = new StringBuilder();
            using var writer = new StringWriter(sb);
            var logger = new JsonLinesStructuredLogger(writer, source: "font-worker");

            logger.Log(StructuredLogLevel.Warning, "worker.not_elevated", "corr-9", new Dictionary<string, object?> { ["pid"] = 1234 });

            string line = sb.ToString().Trim();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal("Warning", root.GetProperty("level").GetString());
            Assert.Equal("font-worker", root.GetProperty("source").GetString());
            Assert.Equal("worker.not_elevated", root.GetProperty("event").GetString());
            Assert.Equal("corr-9", root.GetProperty("correlationId").GetString());
            Assert.Equal(1234, root.GetProperty("pid").GetInt32());
            Assert.True(root.TryGetProperty("ts", out _));
        }

        [Fact]
        public void CompositeLogger_FansOutToAllSinks()
        {
            var a = new InMemoryStructuredLogger();
            var b = new InMemoryStructuredLogger();
            var composite = new CompositeStructuredLogger(a, b);

            composite.Error("boom", "c");

            Assert.Single(a.Entries);
            Assert.Single(b.Entries);
        }

        [Fact]
        public void NullLogger_IsSafeNoOp()
        {
            NullStructuredLogger.Instance.Info("x");
            NullStructuredLogger.Instance.Log(StructuredLogLevel.Error, "y", "c", new Dictionary<string, object?> { ["k"] = "v" });
        }
    }
}
