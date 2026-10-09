namespace GameSaves.Tests;

/// <summary>
/// Tests that create Avalonia controls without a platform share this
/// collection. The first thread to touch <c>Dispatcher.UIThread</c> becomes
/// its owner for the rest of the process, and every later property write
/// from another thread throws "The calling thread cannot access this
/// object". One sequential collection keeps all such tests on one thread;
/// two classes racing for the binding failed 31 of the tab-detach tests.
/// </summary>
[CollectionDefinition(nameof(AvaloniaDispatcherCollection), DisableParallelization = true)]
public sealed class AvaloniaDispatcherCollection
{
}
