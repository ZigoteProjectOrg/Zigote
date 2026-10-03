using Xunit;

namespace Zigote.Tests;

// These tests inspect process-wide counters and versions. Keep other collections
// from mutating the reactive graph while this collection is running.
[CollectionDefinition("Reactive-serial", DisableParallelization = true)]
public sealed class ReactiveCollection;
