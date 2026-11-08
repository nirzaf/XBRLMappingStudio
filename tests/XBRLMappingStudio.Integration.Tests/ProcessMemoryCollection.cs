namespace XBRLMappingStudio.Integration.Tests;

// GC.GetTotalMemory measures the entire test process. Avoid other test classes
// allocating while these workloads run in ordinary local test invocations.
// CI additionally starts a fresh process per 100k-row workload so a completed
// test's async state machine/oracle cannot contaminate the next measurement.
[CollectionDefinition("Process memory qualification", DisableParallelization = true)]
public sealed class ProcessMemoryCollection;
