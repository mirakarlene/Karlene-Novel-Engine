using Xunit;

// WPF pack resources and the system clipboard are shared across STA dispatchers.
// Separate test threads must not load BAML or change clipboard state concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
