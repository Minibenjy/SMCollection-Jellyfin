using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiAssistant.Providers;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// The catalogue of tools the assistant may call.
/// </summary>
public sealed class ToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolRegistry"/> class.
    /// </summary>
    /// <param name="tools">Every registered tool.</param>
    public ToolRegistry(IEnumerable<IAssistantTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the tools available for one exchange, honouring the administrator's settings.
    /// </summary>
    /// <param name="allowMutating">Whether state-changing tools are permitted.</param>
    /// <returns>The available tools.</returns>
    public IReadOnlyList<IAssistantTool> GetAvailable(bool allowMutating)
        => new ReadOnlyCollection<IAssistantTool>(
            _tools.Values.Where(t => allowMutating || !t.IsMutating).ToList());

    /// <summary>
    /// Resolves a tool by the name the model used.
    /// </summary>
    /// <param name="name">Tool name.</param>
    /// <param name="allowMutating">Whether state-changing tools are permitted.</param>
    /// <returns>The tool, or null when the model named something that does not exist.</returns>
    public IAssistantTool? Resolve(string name, bool allowMutating)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return null;
        }

        // A disabled mutating tool is never exposed, so a call naming one is either a
        // hallucination or an attempt to reach past the configured surface. Both are
        // handled the same way: it does not resolve.
        return !tool.IsMutating || allowMutating ? tool : null;
    }

    /// <summary>
    /// Describes in plain language what a tool call would do, for a confirmation prompt.
    /// </summary>
    /// <param name="tool">The tool about to run.</param>
    /// <param name="scope">The acting user, so the description can resolve what the call really touches.</param>
    /// <param name="arguments">Arguments the model supplied.</param>
    /// <returns>A one-line description.</returns>
    public string Describe(IAssistantTool tool, UserScope scope, JsonObject arguments)
        => tool.DescribeCall(scope, arguments);

    /// <summary>
    /// Projects the available tools into provider-neutral definitions.
    /// </summary>
    /// <param name="allowMutating">Whether state-changing tools are permitted.</param>
    /// <returns>Tool definitions for the model.</returns>
    public IReadOnlyList<ChatToolDefinition> Describe(bool allowMutating)
        => GetAvailable(allowMutating)
            .Select(t => new ChatToolDefinition(t.Name, t.Description, t.ParametersSchema))
            .ToList();
}
