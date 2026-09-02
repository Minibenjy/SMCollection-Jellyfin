using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Providers;
using Jellyfin.Plugin.AiAssistant.Providers.Ollama;
using Jellyfin.Plugin.AiAssistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiAssistant.TestHarness;

/// <summary>
/// Exercises the Ollama wire format against a stub server.
/// </summary>
public static class Program
{
    private static int _failures;

    /// <summary>Entry point.</summary>
    /// <returns>Zero when every check passes.</returns>
    public static async Task<int> Main()
    {
        using var stub = new StubOllamaServer(14711);

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var provider = new OllamaProvider(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            LoggerFactory.Create(b => { }).CreateLogger<OllamaProvider>());

        var connection = new ProviderConnection(stub.BaseUrl, null);

        await PlainAnswerIsReturned(provider, connection, stub).ConfigureAwait(false);
        await ToolCallIsParsed(provider, connection, stub).ConfigureAwait(false);
        await StringEncodedArgumentsAreRecovered(provider, connection, stub).ConfigureAwait(false);
        await ToolsAreAdvertised(provider, connection, stub).ConfigureAwait(false);
        await ToolResultsBecomeToolTurns(provider, connection, stub).ConfigureAwait(false);
        await ModelsAreListed(provider, connection, stub).ConfigureAwait(false);
        SloppyToolArgumentsAreCoerced();

        Console.WriteLine(_failures == 0 ? "\nAll checks passed." : $"\n{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task PlainAnswerIsReturned(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        stub.ChatResponse = """
        {"message":{"role":"assistant","content":"You have 12 films."},"done_reason":"stop"}
        """;

        var response = await provider.CompleteAsync(connection, NewRequest(), CancellationToken.None)
            .ConfigureAwait(false);

        Check("plain answer text", response.Message.Text == "You have 12 films.");
        Check("plain answer stops the loop", response.StopReason == ChatStopReason.EndTurn);
    }

    private static async Task ToolCallIsParsed(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        stub.ChatResponse = """
        {"message":{"role":"assistant","content":"","tool_calls":[
          {"function":{"name":"search_library","arguments":{"query":"ghibli","limit":5}}}
        ]},"done_reason":"stop"}
        """;

        var response = await provider.CompleteAsync(connection, NewRequest(), CancellationToken.None)
            .ConfigureAwait(false);

        Check("tool call is detected", response.StopReason == ChatStopReason.ToolUse);
        Check("tool call count", response.Message.ToolCalls.Count == 1);

        var call = response.Message.ToolCalls[0];
        Check("tool name", call.Name == "search_library");
        Check("tool argument", call.Arguments["query"]?.GetValue<string>() == "ghibli");
        Check("synthesized correlation id", !string.IsNullOrEmpty(call.Id));
    }

    private static async Task StringEncodedArgumentsAreRecovered(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        // Some models emit arguments as a JSON string rather than an object.
        stub.ChatResponse = """
        {"message":{"role":"assistant","tool_calls":[
          {"function":{"name":"search_library","arguments":"{\"query\":\"dune\"}"}}
        ]},"done_reason":"stop"}
        """;

        var response = await provider.CompleteAsync(connection, NewRequest(), CancellationToken.None)
            .ConfigureAwait(false);

        Check(
            "string-encoded arguments are parsed",
            response.Message.ToolCalls.Count == 1
            && response.Message.ToolCalls[0].Arguments["query"]?.GetValue<string>() == "dune");
    }

    private static async Task ToolsAreAdvertised(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        stub.ChatResponse = """{"message":{"role":"assistant","content":"ok"},"done_reason":"stop"}""";

        var request = NewRequest();
        request.Tools.Add(new ChatToolDefinition(
            "search_library",
            "Search the library.",
            new JsonObject { ["type"] = "object" }));

        await provider.CompleteAsync(connection, request, CancellationToken.None).ConfigureAwait(false);

        var sent = JsonNode.Parse(stub.LastRequestBody ?? "{}");
        var tools = sent?["tools"]?.AsArray();

        Check("tools are sent", tools is { Count: 1 });
        Check(
            "tool is wrapped as a function",
            tools?[0]?["function"]?["name"]?.GetValue<string>() == "search_library");
        Check("system prompt is sent first",
            sent?["messages"]?[0]?["role"]?.GetValue<string>() == "system");
    }

    private static async Task ToolResultsBecomeToolTurns(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        stub.ChatResponse = """{"message":{"role":"assistant","content":"ok"},"done_reason":"stop"}""";

        var request = NewRequest();
        var results = new ChatMessage { Role = ChatRole.Tool };
        results.ToolResults.Add(new ChatToolResult("call_0", "{\"total\":3}"));
        request.Messages.Add(results);

        await provider.CompleteAsync(connection, request, CancellationToken.None).ConfigureAwait(false);

        var sent = JsonNode.Parse(stub.LastRequestBody ?? "{}");
        var messages = sent?["messages"]?.AsArray();
        var toolTurn = messages?.LastOrDefault();

        Check("tool result becomes a tool turn", toolTurn?["role"]?.GetValue<string>() == "tool");
        Check("tool result content survives", toolTurn?["content"]?.GetValue<string>() == "{\"total\":3}");
    }

    private static async Task ModelsAreListed(
        OllamaProvider provider, ProviderConnection connection, StubOllamaServer stub)
    {
        stub.TagsResponse = """{"models":[{"name":"llama3.1:8b"},{"name":"qwen2.5:7b"}]}""";

        var models = await provider.ListModelsAsync(connection, CancellationToken.None).ConfigureAwait(false);

        Check("models are listed", models.Count == 2 && models[0] == "llama3.1:8b");
    }

    /// <summary>
    /// Small models send the wrong JSON types. A strict read threw, and because the
    /// model never sees the exception it retried the same call until the tool-call
    /// budget was gone — one bad type cost the whole exchange.
    /// </summary>
    private static void SloppyToolArgumentsAreCoerced()
    {
        var numericString = new JsonObject { ["limit"] = "5" };
        Check("numeric string becomes an int", ToolArguments.GetInt(numericString, "limit", 10) == 5);

        var floatValue = new JsonObject { ["limit"] = 7.6 };
        Check("float becomes an int", ToolArguments.GetInt(floatValue, "limit", 10) == 8);

        var missing = new JsonObject();
        Check("missing int falls back", ToolArguments.GetInt(missing, "limit", 10) == 10);

        var garbage = new JsonObject { ["limit"] = "lots" };
        Check("unparseable int falls back", ToolArguments.GetInt(garbage, "limit", 10) == 10);

        var bareString = new JsonObject { ["kinds"] = "Movie" };
        Check(
            "bare string becomes a one-item list",
            ToolArguments.GetStringList(bareString, "kinds") is { Count: 1 } one && one[0] == "Movie");

        var commaSeparated = new JsonObject { ["kinds"] = "Movie, Series" };
        Check(
            "comma-separated string becomes a list",
            ToolArguments.GetStringList(commaSeparated, "kinds") is { Count: 2 } two && two[1] == "Series");

        var arrayAsString = new JsonObject { ["kinds"] = "[\"Movie\",\"Book\"]" };
        Check(
            "array-shaped string is parsed",
            ToolArguments.GetStringList(arrayAsString, "kinds") is { Count: 2 } three && three[1] == "Book");

        var pythonList = new JsonObject { ["kinds"] = "['Movie']" };
        Check(
            "python-style list is cleaned",
            ToolArguments.GetStringList(pythonList, "kinds") is { Count: 1 } py && py[0] == "Movie");

        var pythonPair = new JsonObject { ["kinds"] = "['Movie', 'Series']" };
        Check(
            "python-style pair is cleaned",
            ToolArguments.GetStringList(pythonPair, "kinds") is { Count: 2 } pyp && pyp[1] == "Series");

        var realArray = new JsonObject { ["kinds"] = new JsonArray("Movie", "Episode") };
        Check("real array still works", ToolArguments.GetStringList(realArray, "kinds").Count == 2);

        var numberAsQuery = new JsonObject { ["query"] = 1999 };
        Check("non-string query is read as text", ToolArguments.GetString(numberAsQuery, "query") == "1999");
    }

    private static ChatRequest NewRequest()
    {
        var request = new ChatRequest { Model = "test", SystemPrompt = "You are a test." };
        request.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "hello" });
        return request;
    }

    private static void Check(string name, bool passed)
    {
        Console.WriteLine((passed ? "  PASS  " : "  FAIL  ") + name);
        if (!passed)
        {
            _failures++;
        }
    }
}
