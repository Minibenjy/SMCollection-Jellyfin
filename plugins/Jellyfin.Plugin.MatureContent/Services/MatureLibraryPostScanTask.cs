using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MatureContent.Services;

/// <summary>
/// After every library scan, re-applies the mature tag to any item in the durable
/// history that lost it (e.g. because a metadata refresh replaced its tags).
/// </summary>
public class MatureLibraryPostScanTask : ILibraryPostScanTask
{
    private readonly MaturePolicyService _policyService;
    private readonly ILogger<MatureLibraryPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatureLibraryPostScanTask"/> class.
    /// </summary>
    /// <param name="policyService">The mature policy service.</param>
    /// <param name="logger">The logger.</param>
    public MatureLibraryPostScanTask(MaturePolicyService policyService, ILogger<MatureLibraryPostScanTask> logger)
    {
        _policyService = policyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _policyService.ReapplyMarksAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mature Content: post-scan re-apply failed.");
        }
        finally
        {
            progress.Report(100);
        }
    }
}
