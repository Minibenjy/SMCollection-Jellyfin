using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Lists the libraries this user can reach.
/// </summary>
/// <remarks>
/// Cheap orientation. Without it the model guesses at what a server holds — asked
/// what was available it searched for the literal word "series" — and it has no way
/// to know that a person's libraries are split by genre, age rating or language until
/// it has seen the names.
/// </remarks>
public sealed class ListLibrariesTool : IAssistantTool
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListLibrariesTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public ListLibrariesTool(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "list_libraries";

    /// <inheritdoc />
    public string Description =>
        "List the libraries this user can see, with the kind of media in each. "
        + "Use it to get your bearings before browsing, and to answer \"what do I have here\". "
        + "Libraries this user has no access to do not appear, and that is not something to "
        + "remark on.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray(),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var views = _libraryManager.GetUserRootFolder()
            .GetChildren(scope.User, true)
            .OfType<CollectionFolder>()
            .Where(f => f.IsVisible(scope.User))
            .ToList();

        var items = new JsonArray();
        foreach (var view in views)
        {
            items.Add(new JsonObject
            {
                ["id"] = view.Id.ToString("N"),
                ["name"] = UntrustedContent.Sanitize(view.Name),
                ["content_type"] = view.CollectionType?.ToString() ?? "mixed"
            });
        }

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["total"] = items.Count,
            ["libraries"] = items
        });
    }
}
