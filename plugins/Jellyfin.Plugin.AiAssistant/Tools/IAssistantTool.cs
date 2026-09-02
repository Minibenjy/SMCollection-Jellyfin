using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// A capability the assistant is allowed to invoke.
/// </summary>
/// <remarks>
/// The set of implementations of this interface is the complete list of things the
/// assistant can do. There is deliberately no general-purpose escape hatch — no
/// shell, no filesystem access, no arbitrary HTTP, no SQL. Anything the model is
/// asked to do that no tool covers simply cannot happen, regardless of what the
/// prompt says. This is OWASP LLM06 (Excessive Agency) mitigation by construction
/// rather than by instruction.
/// </remarks>
public interface IAssistantTool
{
    /// <summary>Gets the tool name exposed to the model.</summary>
    string Name { get; }

    /// <summary>Gets the description the model uses to decide when to call this tool.</summary>
    string Description { get; }

    /// <summary>Gets the JSON Schema for this tool's arguments.</summary>
    JsonObject ParametersSchema { get; }

    /// <summary>
    /// Gets a value indicating whether this tool changes server state.
    /// </summary>
    /// <remarks>
    /// Mutating tools are subject to confirmation before they run, so the model can
    /// never create, rename or delete anything without the user agreeing to it.
    /// </remarks>
    bool IsMutating { get; }

    /// <summary>
    /// Describes in plain language what one call would do, for the confirmation prompt.
    /// </summary>
    /// <remarks>
    /// The person approving a write is the only control standing between the model and
    /// their library, so the sentence they read has to say what will actually happen —
    /// which playlist, how many items. Each mutating tool overrides this; the default
    /// is the honest fallback for anything that has not.
    /// </remarks>
    /// <param name="scope">The acting user, so the description can resolve what the call really touches.</param>
    /// <param name="arguments">Arguments the model supplied.</param>
    /// <returns>A one-line description.</returns>
    string DescribeCall(UserScope scope, JsonObject arguments)
        => string.Create(CultureInfo.InvariantCulture, $"Allow the assistant to run {Name}?");

    /// <summary>
    /// Executes the tool as the scoped user.
    /// </summary>
    /// <param name="scope">The acting user. Tools must not act outside it.</param>
    /// <param name="arguments">Arguments supplied by the model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A JSON-serializable result for the model.</returns>
    Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken);
}
