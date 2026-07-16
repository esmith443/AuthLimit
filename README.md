# AuthLimit

Oxide/uMod plugin for Rust. Caps how many players can be authorized on a TC, code lock, or auto turret — stops large teams from stacking auth on one base.

## Install

1. Drop `AuthLimit.cs` in `oxide/plugins/`
2. `oxide.reload AuthLimit`

## Config

`oxide/config/AuthLimit.json`

| Key | Default | Description |
|---|---|---|
| `Max Authorization Limit` | `4` | Max players per entity |
| `Feature Toggles` | all `true` | Enable/disable per entity type (TC, code lock, turret) |
| `Discord Webhook` | off | Webhook URL + cooldown for violation alerts |
| `Enable Debug Logging` | `false` | Verbose console logging |

## Permissions

- `authlimit.bypass` — exempt from the limit
- `authlimit.admin` — access to admin command

```
oxide.grant user <name> authlimit.bypass
```

## Commands

`/authlimit.check` — shows current config (requires `authlimit.admin`)

## Discord Webhooks

On a blocked authorization, sends an embed with entity type, auth count, owner, offending player, and a ready-to-paste teleport command. Rate-limited per player per entity type (default 60s).

## Notes

- Limits apply per entity, not server-wide
- Existing authorizations before install aren't affected
- Key locks aren't supported (no auth list to check)

MIT
