using System.Collections.Generic;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Linux-runnable coverage of the pure PID-diff selection extracted from
    /// PublisherComRenderer. The COM activation, watchdog, and job-object wiring
    /// around it remain Windows-only and are verified by hand on a Publisher box.
    /// </summary>
    public sealed class PublisherComRendererTests
    {
        [Fact]
        public void SelectOwnedPid_returns_new_pid()
        {
            var preExisting = new HashSet<int> { 100, 200 };
            var current = new List<int> { 100, 200, 777 };

            Assert.Equal(777, PublisherComRenderer.SelectOwnedPid(preExisting, current));
        }

        [Fact]
        public void SelectOwnedPid_returns_null_when_no_new_process()
        {
            // COM attached to a pre-existing Publisher instance: no PID appeared
            // that wasn't already running, so we own nothing.
            var preExisting = new HashSet<int> { 100, 200 };
            var current = new List<int> { 100, 200 };

            Assert.Null(PublisherComRenderer.SelectOwnedPid(preExisting, current));
        }
    }
}
