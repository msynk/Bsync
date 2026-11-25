using Xunit;

// Microsoft.Data.Sqlite's connection pools are process-wide, and several tests clear or inspect them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
