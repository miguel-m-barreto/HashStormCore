# HashStormCore Benchmarks

Run benchmarks explicitly in Release mode:

```bash
dotnet run -c Release --project src/HashStormCore.Benchmarks
```

With no arguments, the command runs all benchmarks non-interactively. Pass
BenchmarkDotNet arguments only when you intentionally want filtering or custom
configuration.

Benchmark output is performance data, not a correctness test. Run the normal test
suite before trusting benchmark comparisons. Do not commit
`BenchmarkDotNet.Artifacts`, benchmark reports, or benchmark logs.

The current benchmark set covers the managed Bitcoin submit input-validation and
duplicate-key hot path as standalone equivalents of the production semantics.
Malformed-input benchmarks are named to distinguish cheap predicate validation
from exception-path cost. Predicate benchmarks do not measure exception
allocation; `ExceptionPath` benchmarks do.

Native hashing benchmarks are intentionally not included yet. Add them only when
they can call an existing HashStormCore wrapper and use deterministic correctness
fixtures already present in the repository.

To compare native C++14 and C++17 builds, run the same benchmark command on each
commit or branch and compare the BenchmarkDotNet results under
`BenchmarkDotNet.Artifacts`.
