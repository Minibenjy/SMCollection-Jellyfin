using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>Metadata gap filling: only empty fields, never locked ones, TMDb by the ids the item has.</summary>
public sealed partial class ArtworkRefreshService
{
    private const string MetadataSlot = "Metadata:0";

    private ItemMetadataSnapshot SnapshotOf(BaseItem item, BaseItemKind kind, RunContext context)
        => new()
        {
            Kind = kind,
            LibraryIds = IsCategory(kind) ? [] : item.GetAncestorIds().Where(context.LibraryIds.Contains).ToArray(),
            NativeLocked = item.IsLocked,
            LockedFields = (item.LockedFields ?? []).Select(f => f.ToString()).ToArray(),
            Overview = item.Overview,
            Genres = item.Genres ?? [],
            ProductionYear = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            Studios = item.Studios ?? [],
            Taglines = string.IsNullOrWhiteSpace(item.Tagline) ? [] : [item.Tagline]
        };

    private async Task<bool> ProcessMetadataAsync(BaseItem item, ProcessOptions options, RunContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration;
        var kind = KindOf(item);
        var gaps = MetadataGapPolicy.Gaps(configuration, SnapshotOf(item, kind, context));
        if (gaps.Count == 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var state = State.Get(item.Id, MetadataSlot);
        if (options.Scheduled && state?.NextRetryUtc is { } retry && retry > now)
        {
            return true;
        }

        var query = BuildQuery(item, kind, [], context);
        var remote = await TmdbMetadataSource.FetchAsync(query, Http, configuration, cancellationToken).ConfigureAwait(false);
        var plan = remote is null ? [] : MetadataGapPolicy.Plan(gaps, remote);
        if (plan.Count == 0)
        {
            Bump(s => s.MetadataNoData++);
            State.Touch(item.Id, MetadataSlot, st =>
            {
                st.LastAttemptUtc = now;
                st.FailureCount++;
                st.NextRetryUtc = now.AddDays(Math.Max(1, configuration.RetryAfterDays) * Math.Min(4, st.FailureCount));
            });
            return true;
        }

        if (!options.DryRun)
        {
            // The answer took a while: look again just before writing, and keep only what is still empty and unlocked.
            if (_providerManager.GetRefreshProgress(item.Id) is not null)
            {
                Bump(s => s.SkippedFresh++);
                return true;
            }

            var still = MetadataGapPolicy.Gaps(configuration, SnapshotOf(item, kind, context));
            plan = plan.Where(p => still.Contains(p.Field)).ToList();
            if (plan.Count == 0)
            {
                return true;
            }

            foreach (var fill in plan)
            {
                switch (fill.Field)
                {
                    case MetadataGapPolicy.Overview:
                        item.Overview = (string)fill.Value;
                        break;
                    case MetadataGapPolicy.Genres:
                        item.Genres = (string[])fill.Value;
                        break;
                    case MetadataGapPolicy.ProductionYear:
                        item.ProductionYear = (int)fill.Value;
                        break;
                    case MetadataGapPolicy.CommunityRating:
                        item.CommunityRating = (float)fill.Value;
                        break;
                    case MetadataGapPolicy.Studios:
                        item.Studios = (string[])fill.Value;
                        break;
                    case MetadataGapPolicy.Tagline:
                        item.Tagline = (string)fill.Value;
                        break;
                }
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            State.Touch(item.Id, MetadataSlot, st =>
            {
                st.LastAttemptUtc = now;
                st.LastSuccessUtc = now;
                st.FailureCount = 0;
                st.NextRetryUtc = null;
                st.FilledFields ??= [];
                foreach (var fill in plan)
                {
                    // Which fields this plugin wrote, and a short hash of the value, so it can tell later whether the value is still its own.
                    var text = fill.Value is string[] list ? string.Join("|", list) : Convert.ToString(fill.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    st.FilledFields[fill.Field] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
                }
            });
        }

        Bump(s =>
        {
            s.MetadataItems++;
            foreach (var fill in plan)
            {
                s.MetadataFilled[fill.Field] = (s.MetadataFilled.TryGetValue(fill.Field, out var n) ? n : 0) + 1;
            }
        });
        Append(options.DryRun ? "skip" : "ok", item.Name + " — metadata " + (options.DryRun ? "would fill: " : "filled: ") + string.Join(", ", plan.Select(p => p.Field)));

        return true;
    }
}
