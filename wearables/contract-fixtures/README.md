# Watch contract fixtures

The single source of truth for the messages between the Coo phone app and the Wear OS app (ADR 0009 §6). The .NET tests
(`tests/Coo.Mobile.Application.Tests/WatchContractTests.cs`) read each file into `Coo.Contracts.Wearables` and write it
back unchanged; the Kotlin app's tests must do the same with its data classes. JSON is camelCase; times are Unix
milliseconds.

- `entry-diaper.json`: a wet diaper logged on the watch.
- `entry-breast-stopped.json`: a feed on the left, started and stopped on the watch.
- `entry-sleep-running.json`: a sleep started on the watch, still running.
- `entry-deleted.json`: an entry deleted on the watch after its undo window.
- `snapshot.json`: what the phone publishes for the watch to show.
