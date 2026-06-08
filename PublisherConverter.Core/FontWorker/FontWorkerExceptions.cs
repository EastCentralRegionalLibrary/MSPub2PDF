using System;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>Base type for font-worker client failures.</summary>
    public class FontWorkerException : Exception
    {
        public FontWorkerException(string message) : base(message) { }
        public FontWorkerException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The worker could not be launched, connected, or did not pass its startup
    /// health check within the configured startup timeout. The provisioning
    /// operation that needed elevation fails; the coordinator treats elevation
    /// as unavailable for the remainder of the cycle.
    /// </summary>
    public sealed class FontWorkerStartupException : FontWorkerException
    {
        public FontWorkerStartupException(string message) : base(message) { }
        public FontWorkerStartupException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A request exceeded its timeout. The worker is marked unhealthy.</summary>
    public sealed class FontWorkerTimeoutException : FontWorkerException
    {
        public FontWorkerTimeoutException(string message) : base(message) { }
    }

    /// <summary>
    /// The worker crashed, terminated, or the IPC channel broke mid-request.
    /// The worker is marked unhealthy and its references released.
    /// </summary>
    public sealed class FontWorkerCommunicationException : FontWorkerException
    {
        public FontWorkerCommunicationException(string message) : base(message) { }
        public FontWorkerCommunicationException(string message, Exception inner) : base(message, inner) { }
    }
}
