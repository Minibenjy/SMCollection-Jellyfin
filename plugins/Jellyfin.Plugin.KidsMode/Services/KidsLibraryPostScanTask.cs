using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsMode.Services;

/// <summary>
/// After every library scan, re-applies kids personal tags so a metadata refresh
/// cannot drop them, and refreshes active users' folder restrictions.
/// </summary>
public class KidsLibraryPostScanTask : ILibraryPostScanTask
{
    private readonly KidsPolicyService _policyService;
    private readonly ILogger<KidsLibraryPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsLibraryPostScanTask"/> class.
    /// </summary>
    /// <param name="policyService">The kids policy service.</param>
    /// <param name="logger">The logger.</param>
    public KidsLibraryPostScanTask(KidsPolicyService policyService, ILogger<KidsLibraryPostScanTask> logger)
    {
        _policyService = policyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _policyService.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kids Mode: post-scan reconcile failed.");
        }
        finally
        {
            progress.Report(100);
        }
    }
}
