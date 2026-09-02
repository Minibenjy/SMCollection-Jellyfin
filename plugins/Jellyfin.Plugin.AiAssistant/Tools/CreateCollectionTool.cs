using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Creates a collection, for administrators only.
/// </summary>
/// <remarks>
/// A playlist belongs to the person who made it. A collection does not: it appears in
/// every user's library. That makes it the one capability here whose blast radius
/// exceeds the acting user, so it is gated on the user's own administrator
/// permission — read from Jellyfin, never from the model or the conversation. A
/// non-administrator is told plainly that this is not theirs to do, which is a better
/// answer than a tool that silently is not there.
/// </remarks>
public sealed class CreateCollectionTool : IAssistantTool
{
    private const int MaxItems = 200;

    private readonly ICollectionManager _collectionManager;
    private readonly ItemResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CreateCollectionTool"/> class.
    /// </summary>
    /// <param name="collectionManager">Collection manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public CreateCollectionTool(ICollectionManager collectionManager, ILibraryManager libraryManager)
    {
        _collectionManager = collectionManager;
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "create_collection";

    /// <inheritdoc />
    public string Description =>
        "Create a collection from items already looked up. A collection is shown to EVERY user "
        + "of this server, unlike a playlist, which belongs to one person. Only administrators "
        + "may do this. If the user just wants their own list, use create_playlist instead.";

    /// <inheritdoc />
    public bool IsMutating => true;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Name for the collection."
            },
            ["item_ids"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Ids of items to put in it, as returned by a lookup tool.",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray("name", "item_ids"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var name = ToolArguments.GetString(arguments, "name") ?? "Untitled";
        var count = ToolArguments.GetStringList(arguments, "item_ids").Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Create a collection called \"{UntrustedContent.Sanitize(name)}\" with {count} item(s)? Everyone on this server will see it.");
    }

    /// <inheritdoc />
    public async Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!scope.User.HasPermission(PermissionKind.IsAdministrator))
        {
            return new JsonObject
            {
                ["error"] = "Only an administrator of this server can create a collection. Nothing was created.",
                ["what_to_do"] = "Tell the user this, and offer to make them a playlist instead."
            };
        }

        var name = (ToolArguments.GetString(arguments, "name") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new JsonObject { ["error"] = "A collection name is required." };
        }

        var requested = ToolArguments.GetStringList(arguments, "item_ids");
        if (requested.Count == 0)
        {
            return new JsonObject { ["error"] = "At least one item id is required." };
        }

        var resolution = _resolver.ResolveForPlaylist(scope, requested, MaxItems);
        if (resolution.Items.Count == 0)
        {
            return new JsonObject
            {
                ["error"] = "None of those references resolved to an item in this library. Nothing was created.",
                ["rejected"] = new JsonArray(resolution.Rejected.Select(r => (JsonNode)r).ToArray())
            };
        }

        var collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name,
            ItemIdList = resolution.Items.Select(i => i.Id.ToString("N")).ToArray(),
            UserIds = new[] { scope.UserId }
        }).ConfigureAwait(false);

        return PlaylistReport.Build(
            new JsonObject
            {
                ["created"] = true,
                ["collection_id"] = collection.Id.ToString("N"),
                ["name"] = UntrustedContent.Sanitize(name),
                ["visible_to"] = "every user of this server"
            },
            resolution);
    }
}
