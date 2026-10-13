using Xunit.Sdk;
using Xunit.v3;

// Every test runs in parallel, not just one class at a time: each MySQL test has its own database and each test its own
// work folder, so they share nothing (CLAUDE.md §5 "Tests"). The 45 MySQL tests took 261 s one class at a time.
[assembly: Parallelization(Mode = ParallelMode.All)]
