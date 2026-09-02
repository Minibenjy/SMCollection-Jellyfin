# Kids Mode

Turns any account into a restricted, kid-safe view with one toggle, and puts the
account back exactly as it was when you turn it off.

Where [Mature Content](../Jellyfin.Plugin.MatureContent/) is a blocklist — hide
these things — Kids Mode is an **allow-list**: show *only* these things, and
nothing else.

## What happens when it is on

For the account it is active on, the plugin sets:

- `UserPolicy.AllowedTags` to that account's personal kids tag,
- `EnableAllFolders = false` and `EnabledFolders` to only the libraries holding
  approved content,
- `EnableLiveTvAccess = false`.

The **previous value of all four fields is stashed first** (`SavedPolicies`) and
restored when Kids Mode is switched off. Turning it on and off does not quietly
flatten a user's real permissions.

## How the allow-list works

Each account gets its own tag, `kids_<first 8 hex of the user id>`, kept in sync
with that account's effective list:

```
effective list  =  global KidsItemIds  ∪  override.AddIds  ∖  override.RemoveIds
```

So there is one shared list an administrator curates, and each account can add or
remove items from its own copy without affecting anyone else. The visible `kids`
tag is only a marker for discovering the global list; the per-account tag is what
the policy actually filters on.

## Using it

- **Topbar toggle** — a green "K" appears for any account Kids Mode is enabled
  for, and flips it on and off.
- **Detail page** — a "Kids" button adds or removes the item you are looking at.
- **Multi-select** — two buttons add or remove everything you have selected.

Whether those buttons edit the *global* list or the *account's own override*
depends on who you are: administrators edit the global list, everyone else edits
their own override. An administrator edits someone else's override from the
per-user panel on the plugin's configuration page.

## Configuration

Dashboard → Plugins → Kids Mode.

| Setting | Meaning |
|---|---|
| `KidsItemIds` | The global approved list |
| `DisabledUserIds` | Accounts that may **not** use Kids Mode. Empty by default, so every account can |
| `ActiveUserIds` | Accounts currently in Kids Mode |
| `Overrides` | Per-account `{ UserId, AddIds, RemoveIds }` |
| `SavedPolicies` | The stashed pre-Kids-Mode policy, managed automatically |

## API

`/KidsMode/{State, Items/{id}, Users, Users/{uid}/Enabled, Users/{uid}/Active,
AdminItems, Users/{uid}/Items, Users/{uid}/Items/{iid}, Sync, ClientScript}`.

## Known limitations

- Disabling Kids Mode for a user does not clean up their now-unused
  `kids_<uid8>` tags. They are inert, but they stay on the items.
- This restricts what the Jellyfin **library** shows. It is not a substitute for
  supervision, and it does not filter anything outside Jellyfin.
- Verified behaviour: toggling restores the policy, per-account overrides diverge
  correctly, and the state survives a server restart.
