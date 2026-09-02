using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Marks items watched or unwatched for the acting user.
/// </summary>
/// <remarks>
/// Writes into this user's own playback state and nowhere else, so a series marked
/// watched here is watched for them and unchanged for everybody else on the server.
/// A container — a series or a season — applies to everything inside it, which is
/// what "I've seen all of season 1" means.
/// </remarks>
public sealed class SetWatchedTool : IAssistantTool
{
    private const int MaxItems = 500;

    private readonly ItemResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetWatchedTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userData">User data manager.</param>
    public SetWatchedTool(ILibraryManager libraryManager, IUserDataManager userData)
    {
        _libraryManager = libraryManager;
        _userData = userData;
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "set_watched";

    /// <inheritdoc />
    public string Description =>
        "Mark items as watched or unwatched for this user. "
        + "A series, season or film can be given by name. An EPISODE must be given by id — "
        + "get it from list_episodes first, because episode names are rarely searchable and a "
        + "name like \"Show - Episode 13\" matches nothing. "
        + "A series or season marks everything inside it. This changes only this user's own "
        + "playback state.";

    /// <inheritdoc />
    public bool IsMutating => true;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Names or ids of what to mark.",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["watched"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "True to mark watched, false to mark unwatched."
            }
        },
        ["required"] = new JsonArray("items", "watched"),
        ["additionalProperties"] = false
    };

    /// <remarks>
    /// Names alone were not enough: "mark Kim Possible as watched" and "mark episode 4
    /// as watched" read almost the same in a confirmation built from names, but one
    /// changes one episode and the other changes every episode the series has. This
    /// resolves and expands the same way ExecuteAsync will, so the count in the prompt
    /// is the count that is actually about to change.
    /// </remarks>
    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var names = ToolArguments.GetStringList(arguments, "items");
        var watched = ToolArguments.GetBool(arguments, "watched", true);

        var affected = 0;
        foreach (var reference in names)
        {
            var item = _resolver.Resolve(scope, reference);
            if (item is not null)
            {
                affected += Expand(scope, item).Count;
            }
        }

        var what = UntrustedContent.Sanitize(string.Join(", ", names.Take(3)))
                   + (names.Count > 3 ? "…" : string.Empty);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Mark {what} ({affected} item(s)) as {(watched ? "watched" : "unwatched")} for you?");
    }

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var references = ToolArguments.GetStringList(arguments, "items");
        if (references.Count == 0)
        {
            return Task.FromResult<JsonNode>(new JsonObject { ["error"] = "At least one item is required." });
        }

        var watched = ToolArguments.GetBool(arguments, "watched", true);

        var changed = new JsonArray();
        var notFound = new JsonArray();
        var count = 0;

        foreach (var reference in references)
        {
            var item = _resolver.Resolve(scope, reference);
            if (item is null)
            {
                notFound.Add(UntrustedContent.Sanitize(reference));
                continue;
            }

            var targets = Expand(scope, item);
            foreach (var target in targets.Take(MaxItems - count))
            {
                Apply(scope, target, watched);
                count++;
            }

            changed.Add(new JsonObject
            {
                ["name"] = UntrustedContent.Sanitize(item.Name),
                ["items_affected"] = targets.Count
            });
        }

        var payload = new JsonObject
        {
            ["watched"] = watched,
            ["items_affected"] = count,
            ["changed"] = changed
        };

        if (notFound.Count > 0)
        {
            payload["not_found"] = notFound;
            payload["you_must_tell_the_user"] = "These were not found and were not changed. Say so.";
            payload["hint"] = "A compound name like \"Show - Episode 13\" never resolves. For an "
                              + "episode, call list_episodes for the series and pass the episode's id.";
        }

        return Task.FromResult<JsonNode>(payload);
    }

    private IReadOnlyList<BaseItem> Expand(UserScope scope, BaseItem item)
    {
        if (item is not Folder)
        {
            return new[] { item };
        }

        var children = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = ItemProjection.PlayableKinds.ToArray(),
            Recursive = true,
            ParentId = item.Id,
            Limit = MaxItems
        });

        return children.Items.ToList();
    }

    private void Apply(UserScope scope, BaseItem item, bool watched)
    {
        var data = _userData.GetUserData(scope.User, item);
        if (data is null)
        {
            return;
        }

        data.Played = watched;
        data.PlaybackPositionTicks = 0;

        if (!watched)
        {
            data.PlayCount = 0;
        }

        _userData.SaveUserData(
            scope.User,
            item,
            data,
            UserDataSaveReason.TogglePlayed,
            CancellationToken.None);
    }
}
