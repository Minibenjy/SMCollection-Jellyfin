using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MatureContent.Services;

/// <summary>
/// Applies locked mature-content defaults when the server starts.
/// </summary>
public class MatureStartupHostedService : IHostedService
{
    private readonly MaturePolicyService _policyService;
    private readonly ILogger<MatureStartupHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatureStartupHostedService"/> class.
    /// </summary>
    /// <param name="policyService">The mature policy service.</param>
    /// <param name="logger">The logger.</param>
    public MatureStartupHostedService(MaturePolicyService policyService, ILogger<MatureStartupHostedService> logger)
    {
        _policyService = policyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Mature Content: applying locked user defaults.");
        await _policyService.ApplyLockedDefaultsAsync().ConfigureAwait(false);

        try
        {
            await _policyService.ReapplyMarksAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mature Content: re-applying marks on startup failed.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
