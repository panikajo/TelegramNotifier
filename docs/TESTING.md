# Verification notes

The test executable exercises the real plugin class against DispatchProxy-based fake Swiftly services and a fake HTTP handler; it does not send requests to Telegram.

The 52 assertions cover:

- startup send and minute timer conversion;
- join/leave/map events not scheduling a one-second status send;
- the scheduled status using the latest map/online count;
- immediate connection notices, bot filtering, and duplicate authorization suppression;
- unchanged-status suppression and configured periodic unchanged posts;
- legacy seconds, minutes precedence, case-insensitive JSON keys, nested/flat config, and interval clamping;
- status disabled independently of player-connection messages;
- no overlapping sends, failed send retry, cancellation, unload, hot reload, and old-timer isolation.

Local verification: 52 passed. Build/publish succeeded for .NET 10 and SwiftlyS2 SDK 1.4.10. Real Telegram API and live CS2 have not been exercised.
