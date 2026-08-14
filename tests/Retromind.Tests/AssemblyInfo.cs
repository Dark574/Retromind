using Xunit;

// Several portability tests temporarily change process-wide AppImage environment
// variables. Serial execution keeps those tests isolated from the rest of the suite.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
