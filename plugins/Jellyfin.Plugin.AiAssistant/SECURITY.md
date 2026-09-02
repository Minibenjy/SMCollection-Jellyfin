# Security model

This plugin gives a language model a voice inside your media server. That is a
meaningful trust decision, so this document states plainly what the design
protects against and what it does not.

## Authorization

The assistant has no permissions of its own. Every tool call executes inside a
`UserScope` built from the authenticated caller, and library queries are
constructed with Jellyfin's own `InternalItemsQuery(user)`, so library access,
parental rating ceilings and blocked tags are enforced by the server exactly as
they are for any other request.

No tool accepts a user id as an argument. There is no code path by which the
model can ask for another user's data, because the question cannot be expressed.
An administrator using the assistant gets their own administrative view of the
library and nothing more — the assistant never runs with elevated rights.

## Capability surface

The complete list of things the assistant can do is the set of registered
`IAssistantTool` implementations. There is no shell, no filesystem access, no
arbitrary HTTP, no database access and no code execution, and no tool returns
file paths, configuration values, logs or internal identifiers.

This matters more than the system prompt. Prompt instructions reduce off-topic
answers, but they can be argued around; a capability that does not exist cannot
be invoked no matter how the request is phrased. This is the OWASP LLM06
(Excessive Agency) mitigation of minimizing extensions, functionality and
permissions, applied by construction.

State-changing tools are additionally gated: they can be disabled server-wide by
the administrator, and when enabled they require the user to confirm the action
before it runs (LLM06 human-in-the-loop).

## Untrusted content

Library metadata is untrusted input. Overviews and titles arrive from external
metadata providers and from NFO files that anyone with write access to the media
folder can edit, so a synopsis can contain text engineered to read as an
instruction — OWASP LLM01, indirect prompt injection.

Metadata is therefore fenced in `<library_data>` markers before it reaches the
model, with the delimiters stripped from the content so it cannot close its own
fence, and the system prompt states that fenced content is data rather than
instructions. Fencing raises the cost of an injection; it does not eliminate it.
The reason an injection is not catastrophic here is the capability surface above:
a successful injection can make the assistant say something wrong, but it cannot
make it do anything the acting user could not already do.

## Credential storage

Provider credentials are **encrypted, not hashed**. A hash is one-way, and the
plugin must recover the original secret to authenticate against the provider, so
hashing is not an option for this class of secret.

- Secrets are encrypted with AES-256-GCM and stored in a vault file separate
  from the plugin's XML configuration.
- The vault key is generated on first run and written with owner-only
  permissions, outside the configuration directory that administrators read,
  edit and back up.
- No API returns a stored credential. The configuration UI displays only a
  masked hint (the last four characters).
- Each user's credential is stored under their own id and is never readable
  through another user's session.

**What this does not protect against.** Anyone with root, with the Jellyfin
service account, or with a filesystem backup that includes the vault key can
decrypt stored credentials. No server-side plugin can prevent that — the process
must be able to read the secret in order to use it. What the design does prevent
is casual exposure: through the admin dashboard, in configuration backups, in
support screenshots, in log output, and to other users of the server.

If you do not want your server owner to be able to reach your provider
credential under any circumstances, use a provider that issues revocable,
scoped, individually-billed keys, and rotate them.

## User-supplied endpoints

Letting each user point the assistant at their own backend means an authenticated
user can cause the server to make outbound HTTP requests to an address they
choose — server-side request forgery in shape, if not in intent. It is inherent to
the feature: self-hosted backends live on the local network, so an allow-list of
public hosts would defeat the point.

What bounds it:

- Only **authenticated** users can set an endpoint, and each one only for
  themselves.
- Administrators can turn per-user providers off entirely
  (`AllowUserProviders`), or restrict which providers may be selected
  (`AllowedProviders`), and both are re-checked on every request.
- Responses are parsed as provider payloads and never echoed back verbatim, so
  the endpoint is not usable as a general-purpose fetch-and-display proxy.
- Failures are reported to the user as a generic message; upstream response
  bodies go only to the server log.

Operators who consider this unacceptable for their deployment should disable
per-user providers and configure a single server default.

## Consumption limits

Per-user hourly request limits and a per-exchange tool-call ceiling are enforced
server-side, so a runaway loop or an abusive user cannot silently drain a paid
API account (OWASP LLM10, Unbounded Consumption).

## Reporting

Report suspected vulnerabilities through a private security advisory on the
repository rather than a public issue.
