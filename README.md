# TelegramNotifier for SwiftlyS2

A small SwiftlyS2 plugin that sends CS2 server-status snapshots and player-connection notices to Telegram. Status messages support a **minute-based interval**; player join notifications remain immediate.

> Current project version: **1.1.1** · SwiftlyS2 SDK **1.4.10** · .NET **10**

## Features

- Posts server hostname, map, and online count to a configured Telegram chat/topic.
- Configurable status schedule: `ServerStatus.SendIntervalMinutes` (1–1440; default 5 minutes).
- Player count/map events do **not** bypass that schedule; the next status uses the latest values.
- Optional repeated unchanged status, or suppress duplicates after successful delivery.
- Separate destination and forum topic for player-connection notices.
- Optional bot filtering and quiet delivery.
- English, Ukrainian, and Russian templates; Telegram HTML escaping for dynamic names.
- JSONC config, legacy seconds-setting compatibility, cancellation-safe lifecycle, and a fake-host test suite.

## Quick install

1. Stop the CS2 server and back up the current plugin folder/configuration.
2. Publish/copy `TelegramNotifier.dll` and its `.deps.json` to:

   ```text
   game/csgo/addons/swiftlys2/plugins/TelegramNotifier/
   ```

3. Start once to create the configuration at:

   ```text
   game/csgo/addons/swiftlys2/configs/plugins/TelegramNotifier/telegram_notifier.jsonc
   ```

4. Set a real bot token and chat IDs in the **server config only**. Do not commit that file or share the token.
5. Configure `ServerStatus.SendIntervalMinutes`, then restart the plugin/server.

A sample configuration is in [`TelegramNotifier/telegram_notifier.example.jsonc`](TelegramNotifier/telegram_notifier.example.jsonc). Existing server configs are not overwritten by plugin updates; add new fields by hand.

## Configure the status interval

Add this property **inside the existing `ServerStatus` object**, keeping your current IDs and other settings:

```jsonc
"ServerStatus": {
  "Enabled": true,
  "ChatId": 123456789,
  "MessageThreadId": 0,
  "DisableNotification": true,
  "SendIntervalMinutes": 5,
  "SendUnchangedPeriodicStatus": false
}
```

- `SendIntervalMinutes`: whole minutes from 1 to 1440. Five means a status is evaluated every five minutes.
- On plugin load, one initial status is sent; later evaluations run only on the schedule.
- Join/leave and map-load events no longer send a status after one second. They only update the data used by the next scheduled status.
- `SendUnchangedPeriodicStatus: false` suppresses identical statuses that were successfully delivered. Set `true` to post a status on each interval even when unchanged.
- `PlayerConnections` messages remain immediate and independent of the server-status interval.
- The old `SendOnPlayerCountChange` field is ignored; it can be removed.
- Legacy `SendIntervalSeconds` is supported if `SendIntervalMinutes` is absent and is rounded up: 300→5 min, 90→2 min, 15→1 min. If both are present, minutes take precedence.

The plugin logs its effective interval at load. A failed status send is retried on the next scheduled call, not on every player event; overlapping HTTP sends are skipped.

## Configuration fields

| Path | Default | Purpose |
|---|---:|---|
| `Version` | `3` | Config format marker; existing config is preserved. |
| `Language` | `uk` | Translation file code: `en`, `uk`, or `ru`. |
| `BotToken` | empty | Telegram Bot API token. Store privately on the server. |
| `ServerStatus.Enabled` | `true` | Enables initial and scheduled status messages. |
| `ServerStatus.ChatId` | `0` | Destination chat/channel/supergroup ID. |
| `ServerStatus.MessageThreadId` | `0` | Forum topic ID, or zero. |
| `ServerStatus.DisableNotification` | `true` | Send silently. |
| `ServerStatus.SendIntervalMinutes` | `5` | Status schedule in minutes. |
| `ServerStatus.SendUnchangedPeriodicStatus` | `false` | Send identical status snapshots on schedule. |
| `PlayerConnections.Enabled` | `true` | Enables immediate player connection notices. |
| `PlayerConnections.ChatId` | `0` | Connection notices destination. |
| `PlayerConnections.MessageThreadId` | `0` | Connection topic ID, or zero. |
| `PlayerConnections.DisableNotification` | `true` | Send connection notices silently. |
| `PlayerConnections.IgnoreBots` | `true` | Do not notify for bots. |

A chat ID of `0` disables delivery to that destination. Add the bot to each destination chat; a channel usually requires posting permission.

## Message placeholders

Status: `{HOSTNAME}`, `{MAP}`, `{CURRENT}`, `{MAX}`.  
Player connection: `{NAME}`, `{STEAMID}`, `{AUTH}`.

Templates live at `TelegramNotifier/resources/language/`. Dynamic player/hostname/map strings are HTML-escaped before insertion.

## Build and test

Requires .NET 10 SDK and network access to restore the pinned SwiftlyS2 SDK package.

```sh
dotnet build TelegramNotifier/TelegramNotifier.csproj -c Release
dotnet run --project tests/tests.csproj -c Release
dotnet publish TelegramNotifier/TelegramNotifier.csproj -c Release -o publish/TelegramNotifier
```

GitHub Actions runs the build, tests, and publish on pushes/PRs to `main`; the compiled plugin is uploaded as a workflow artifact. The test suite uses a fake Swiftly host and intercepts HTTP, so it never contacts Telegram.

Latest local verification: **52 assertions passed**, build/publish successful, zero Telegram requests. A live CS2 server has not been tested in this environment.

## Security

- Never put a real Telegram bot token in Git, issues, screenshots, or chat.
- `.gitignore` excludes runtime `telegram_notifier.jsonc`, build output, and logs.
- The checked-in config is an example and must contain only placeholders.
- If a token was exposed, revoke it with BotFather and issue a replacement.

## Provenance and license

This is the SwiftlyS2 TelegramNotifier implementation developed for the user's server and updated to version 1.1.1. The `LICENSE` file is intentionally omitted until the repository owner selects a license. Third-party package dependencies retain their own licenses.
