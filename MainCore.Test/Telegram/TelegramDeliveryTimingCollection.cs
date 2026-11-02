namespace MainCore.Test.Telegram;

// These tests verify paced background delivery against real elapsed-time bounds.
// Heavy parallel browser/SQLite tests can starve Rx's scheduled notification
// delivery; isolate the timing tests without weakening their deadlines/assertions.
[CollectionDefinition("Telegram delivery timing", DisableParallelization = true)]
public sealed class TelegramDeliveryTimingCollection;
