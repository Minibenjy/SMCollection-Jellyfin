using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsMode.Services;

/// <summary>
/// On startup, re-asserts kids-mode policies and personal tags for consistency.
/// </summary>
public class KidsStartupHostedService : IHostedService
{
    private readonly KidsPolicyService _policyService;
    private readonly ILogger<KidsStartupHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsStartupHostedService"/> class.
    /// </summary>
    /// <param name="policyService">The kids policy service.</param>
    /// <param name="logger">The logger.</param>
    public KidsStartupHostedService(KidsPolicyService policyService, ILogger<KidsStartupHostedService> logger)
    {
        _policyService = policyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Kids Mode: ensuring consistency on startup.");
            await _policyService.EnsureConsistentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kids Mode: startup consistency pass failed.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
