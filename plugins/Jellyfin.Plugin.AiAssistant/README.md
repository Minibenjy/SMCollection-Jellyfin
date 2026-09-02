# Jellyfin AI Assistant

A conversational assistant for your Jellyfin library, where **each user brings
their own AI provider** and the assistant can only ever do what that user could
already do themselves.

Works with stock Jellyfin 10.11. No fork, no patched web client.

## What it does

Ask about your library in your own words:

- *"What do I have from Studio Ghibli that I haven't watched?"*
- *"Which episode of The Expanse was I on?"*
- *"Make me a collection of 90s sci-fi from what's here."*

## Design

**Bring your own provider.** Every user routes their own backend — a local
Ollama, an OpenAI-compatible endpoint, Anthropic, OpenRouter. No provider is
privileged: `IChatProvider` is the single place any vendor is mentioned, and
adding one means writing one adapter and touching nothing else. Providers that
cannot call tools are supported and degrade honestly rather than guessing.

**The user's permissions, never more.** Tools execute inside a `UserScope`
derived from the authenticated caller, and library queries go through Jellyfin's
own user-scoped query path, so parental ratings and library access are enforced
by the server. The assistant never holds administrative rights, including for
administrators.

**A capability surface, not a sandbox.** What the assistant can do is exactly the
set of registered tools — no shell, no filesystem, no arbitrary HTTP, no code
execution. Guardrails that matter are enforced in code; the system prompt handles
tone and scope, not security. See [SECURITY.md](SECURITY.md).

**Secrets stay secret from the dashboard.** Credentials are encrypted with
AES-256-GCM in a vault outside the plugin's XML configuration, are never returned
by any endpoint, and appear in the UI only as a masked hint. Read the threat
model in [SECURITY.md](SECURITY.md) before deciding whether that is enough for
you — it is explicit about what a server administrator can still reach.

## Where users configure their provider

The plugin's configuration page lives in the Jellyfin dashboard, which only
administrators can open. Regular users therefore configure their own provider
from a settings view inside the assistant panel itself — the gear icon in its
header — which talks to the same per-user endpoints. Nobody needs an
administrator to point the assistant at their own model.

The gear appears only while the administrator permits per-user providers.

## Tools

Everything the assistant can do is one of these. There is no shell, no filesystem,
no arbitrary HTTP — a request no tool covers simply cannot happen.

| Tool | Writes | What it does |
| --- | --- | --- |
| `list_libraries` | no | The libraries this user can reach |
| `search_library` | no | Search or browse, with genre, watched and favourite filters, and `sort="random"` |
| `get_item_details` | no | The full library record for one title, including this user's watched state |
| `list_episodes` | no | The real episode names one series has here |
| `pick_episodes` | no | Episodes from several series at once, in order or at random |
| `continue_watching` | no | What is part-played, and the next unwatched episode of series already begun |
| `list_playlists` | no | This user's playlists |
| `get_playlist` | no | What is inside one of them |
| `create_playlist` | **yes** | Creates a playlist; refuses to duplicate a name that already exists |
| `add_to_playlist` | **yes** | Extends an existing playlist |
| `remove_from_playlist` | **yes** | Takes items out of one |
| `manage_playlist` | **yes** | Renames or deletes one |
| `set_watched` | **yes** | Marks items watched or unwatched, for this user only |
| `set_favorite` | **yes** | Adds to or removes from this user's favourites |
| `create_collection` | **yes** | Creates a server-wide collection — administrators only |

Every writing tool is described in plain language and shown to the user for
approval before it runs. Nothing writes on the model's say-so.

### Designed for small models

The models people actually point at a home server are 7B-class, and they do not
follow long lists of rules. So the rules live in the tools rather than the
prompt, and the recurring failures are handled as mechanisms:

- A search that over-specifies is automatically retried with fewer words.
- An unknown `kinds` value fails loudly instead of silently becoming no filter.
- An identical tool call repeated inside one exchange returns the previous
  result and an instruction to change approach, rather than running again.
- Item references are re-resolved against the user, so a hallucinated id, an id
  belonging to something they cannot see, or a `playlist_id` fed back as an item
  cannot end up in their library. Containers are expanded and the expansion is
  reported.
- Playlist writes report what actually went in and what did not, by name, so the
  model cannot announce a result it did not get.
- A tool call the model writes into its reply text instead of emitting is
  recovered and run, if it names a tool that was offered.

## Administrator settings

The plugin configuration page controls whether users may bring their own
provider, which providers are permitted, an optional server-wide default
(including whether its API key may be shared), per-user hourly rate limits, and
whether the assistant may create collections and playlists at all.

## Authentication

API keys are supported for every provider. **OpenRouter** additionally supports
signing in with OAuth PKCE, so users get a key without copying and pasting one.

Anthropic and OpenAI do **not** offer OAuth for third-party applications:
Anthropic explicitly prohibits using consumer-plan OAuth tokens in third-party
tools, so this plugin does not implement it — doing so would put your users'
accounts at risk. Use an API key from the provider's console instead.

## Installing and building

Install it from the [SMCollection repository](../../README.md#install) like any
other plugin in this collection. To build just this one from source:

```sh
dotnet publish plugins/Jellyfin.Plugin.AiAssistant -c Release -o out
```

`../../build.sh` builds and packages every plugin, including this one.

## Providers

| Provider | Credential | Tool calling | Status |
| --- | --- | --- | --- |
| Ollama (self-hosted) | none | yes | implemented |
| OpenAI-compatible | API key | yes | planned |
| Anthropic | API key | yes | planned |
| OpenRouter | API key or OAuth PKCE | yes | planned |

Ollama is the recommended default: no key to store, no per-request cost, and
nothing about your library leaves your hardware.

## Testing

`TestHarness` exercises the provider wire format against a stub server, so the
translation layer is checked without needing a running model or a paid API:

```sh
dotnet run --project tests/Jellyfin.Plugin.AiAssistant.TestHarness
```

It exits non-zero on failure, so it runs unattended in CI on every push.

## Status

Working end to end with Ollama: floating launcher, chat panel, agent loop, the
full tool set above, per-user routing, administrator policy and rate limiting are
all in place. The remaining provider adapters and OAuth PKCE are next.

## Security

The threat model — where the authorization boundary is, how credentials are
stored, and what is deliberately *not* trusted — is written up in
[SECURITY.md](SECURITY.md) next to this file.

## License

GPL-3.0, matching Jellyfin. Part of the
[SMCollection](https://github.com/Minibenjy/SMCollection-Jellyfin) plugin
collection.
