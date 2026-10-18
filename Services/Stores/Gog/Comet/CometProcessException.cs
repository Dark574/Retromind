using System;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed class CometProcessException : Exception
{
    internal CometProcessException(string message)
        : base(message)
    {
    }

    internal CometProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
