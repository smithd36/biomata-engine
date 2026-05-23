using System;

namespace Biomata.SDK
{
    public class BiomataException : Exception
    {
        public string TransportCode { get; }
        public string ServerError { get; }

        public BiomataException(string message)
            : base(message) { }

        public BiomataException(string message, Exception inner)
            : base(message, inner) { }

        public BiomataException(string message, string serverError)
            : base(message)
        {
            ServerError = serverError;
        }

        public BiomataException(string message, string transportCode, Exception inner = null)
            : base(message, inner)
        {
            TransportCode = transportCode;
        }

        public bool IsTransient =>
            TransportCode == "timeout" ||
            TransportCode == "disconnected" ||
            TransportCode == "unavailable";

        public static void ThrowIfFailed(bool success, string error, string context)
        {
            if (!success)
                throw new BiomataException($"{context} failed: {error}", error);
        }
    }
}