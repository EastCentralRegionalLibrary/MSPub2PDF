using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Length-prefixed message framing shared by every pipe transport in the
    /// solution (the Publisher render worker and the elevated font worker).
    ///
    /// Wire format: a single message is [4-byte little-endian length][payload].
    /// We can't hand a long-lived duplex pipe straight to
    /// JsonSerializer.(Des)erializeAsync because the deserializer keeps reading
    /// until its buffer fills past a threshold OR the stream returns EOF —
    /// neither happens on a persistent pipe, so it blocks indefinitely even
    /// after a complete value has arrived. Framing each message with its byte
    /// length lets us read exactly the right number of bytes.
    /// </summary>
    internal static class PipeMessageFraming
    {
        public const int MaxMessageBytes = 16 * 1024 * 1024; // 16 MiB safety cap

        public static async Task WriteFramedAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static void WriteFramed(Stream stream, byte[] payload)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            stream.Write(header);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>
        /// Reads one framed message. Returns null when the stream is cleanly at
        /// EOF before any bytes of the next message arrive (peer closed the
        /// pipe between messages).
        /// </summary>
        public static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            if (!await TryReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            ValidateLength(length);
            if (length == 0) return Array.Empty<byte>();

            byte[] payload = new byte[length];
            if (!await TryReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException("Pipe closed mid-message.");
            }
            return payload;
        }

        public static byte[]? ReadFramed(Stream stream)
        {
            byte[] header = new byte[4];
            if (!TryReadExactly(stream, header)) return null;

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            ValidateLength(length);
            if (length == 0) return Array.Empty<byte>();

            byte[] payload = new byte[length];
            if (!TryReadExactly(stream, payload))
            {
                throw new EndOfStreamException("Pipe closed mid-message.");
            }
            return payload;
        }

        private static void ValidateLength(int length)
        {
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"Refusing to read framed message of declared size {length}.");
            }
        }

        private static async Task<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) return total == 0 ? false : throw new EndOfStreamException("Pipe closed mid-message.");
                total += read;
            }
            return true;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) return total == 0 ? false : throw new EndOfStreamException("Pipe closed mid-message.");
                total += read;
            }
            return true;
        }
    }
}
