using System;

namespace Jellyfin.Plugin.AiAssistant.Providers;

/// <summary>
/// Raised when a provider backend fails in a way the user should be told about.
/// </summary>
/// <remarks>
/// Messages on this exception reach the end user, so they must never carry
/// endpoint URLs, credentials or upstream response bodies.
/// </remarks>
public class ProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    public ProviderException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">User-safe description of the failure.</param>
    public ProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">User-safe description of the failure.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
