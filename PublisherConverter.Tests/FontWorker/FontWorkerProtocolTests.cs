using System.Collections.Generic;
using System.Text.Json;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    public class FontWorkerProtocolTests
    {
        private static T RoundTrip<T>(T value)
        {
            string json = JsonSerializer.Serialize(value, FontWorkerProtocol.JsonOptions);
            return JsonSerializer.Deserialize<T>(json, FontWorkerProtocol.JsonOptions)!;
        }

        [Fact]
        public void Request_Envelope_RoundTrips_WithCorrelationAndPayload()
        {
            var request = new FontWorkerRequest
            {
                CorrelationId = "abc123",
                Type = FontWorkerMessageType.InstallCapability,
                InstallCapability = new InstallCapabilityRequest { CapabilityName = "Language.Fonts.Hant~~~und-HANT~0.0.1.0" },
            };

            var copy = RoundTrip(request);

            Assert.Equal(FontWorkerProtocol.Version, copy.ProtocolVersion);
            Assert.Equal("abc123", copy.CorrelationId);
            Assert.Equal(FontWorkerMessageType.InstallCapability, copy.Type);
            Assert.NotNull(copy.InstallCapability);
            Assert.Equal("Language.Fonts.Hant~~~und-HANT~0.0.1.0", copy.InstallCapability!.CapabilityName);
            Assert.Null(copy.InstallFont);
        }

        [Fact]
        public void BatchResponse_RoundTrips_WithPerItemOutcomes()
        {
            var response = new FontWorkerResponse
            {
                CorrelationId = "c1",
                Type = FontWorkerMessageType.InstallCapabilitiesBatch,
                InstallCapabilitiesBatch = new InstallCapabilitiesBatchResponse
                {
                    Results = new List<InstallCapabilityResponse>
                    {
                        new() { CapabilityName = "A", Outcome = InstallOutcome.Installed },
                        new() { CapabilityName = "B", Outcome = InstallOutcome.AlreadyPresent },
                        new() { CapabilityName = "C", Outcome = InstallOutcome.Failed, Detail = "boom" },
                    },
                },
            };

            var copy = RoundTrip(response);

            Assert.Equal(3, copy.InstallCapabilitiesBatch!.Results.Count);
            Assert.Equal(InstallOutcome.Installed, copy.InstallCapabilitiesBatch.Results[0].Outcome);
            Assert.Equal(InstallOutcome.AlreadyPresent, copy.InstallCapabilitiesBatch.Results[1].Outcome);
            Assert.Equal("boom", copy.InstallCapabilitiesBatch.Results[2].Detail);
        }

        [Fact]
        public void Enums_Serialize_AsStrings_ForReadableLogsAndVersioning()
        {
            string json = JsonSerializer.Serialize(new FontWorkerRequest { Type = FontWorkerMessageType.Shutdown }, FontWorkerProtocol.JsonOptions);
            Assert.Contains("\"Shutdown\"", json);
            Assert.DoesNotContain("\"Type\":4", json);
        }

        [Fact]
        public void NewCorrelationId_ProducesDistinctNonEmptyIds()
        {
            string a = FontWorkerProtocol.NewCorrelationId();
            string b = FontWorkerProtocol.NewCorrelationId();
            Assert.False(string.IsNullOrWhiteSpace(a));
            Assert.NotEqual(a, b);
        }

        [Theory]
        [InlineData(InstallOutcome.Installed, true)]
        [InlineData(InstallOutcome.AlreadyPresent, true)]
        [InlineData(InstallOutcome.Failed, false)]
        [InlineData(InstallOutcome.Unsupported, false)]
        public void InstallOutcome_IsSuccess_TreatsAlreadyPresentAsResolved(InstallOutcome outcome, bool expected)
        {
            Assert.Equal(expected, outcome.IsSuccess());
        }

        [Fact]
        public void DefaultProtocolVersion_IsStampedOnNewEnvelopes()
        {
            Assert.Equal(FontWorkerProtocol.Version, new FontWorkerRequest().ProtocolVersion);
            Assert.Equal(FontWorkerProtocol.Version, new FontWorkerResponse().ProtocolVersion);
        }
    }
}
