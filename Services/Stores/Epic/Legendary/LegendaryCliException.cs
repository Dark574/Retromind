using System;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed class LegendaryCliException : Exception
{
    internal LegendaryCliException(string message)
        : base(message)
    {
    }

    internal LegendaryCliException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
