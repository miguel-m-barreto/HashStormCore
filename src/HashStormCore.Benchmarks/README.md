# HashStormCore Benchmarks

Run benchmarks explicitly in Release mode:

```bash
dotnet run -c Release --project src/HashStormCore.Benchmarks
```

With no arguments, the command runs all benchmarks non-interactively. Pass
BenchmarkDotNet arguments only when you intentionally want filtering or custom
configuration.

Examples:

```bash
dotnet run -c Release --project src/HashStormCore.Benchmarks -- --anyCategories DuplicateKey
dotnet run -c Release --project src/HashStormCore.Benchmarks -- --anyCategories DuplicateRegister
dotnet run -c Release --project src/HashStormCore.Benchmarks -- --anyCategories ExtraNonce2
dotnet run -c Release --project src/HashStormCore.Benchmarks -- --anyCategories ExceptionPath
```

Benchmark output is performance data, not a correctness test. Run the normal test
suite before trusting benchmark comparisons. Do not commit
`BenchmarkDotNet.Artifacts`, benchmark reports, or benchmark logs.

The current benchmark set covers the managed Bitcoin submit input-validation and
duplicate-key hot path. `Production_*` benchmarks call the extracted production
helpers in `HashStormCore.Blockchain.Bitcoin`; `StandaloneEquivalent_*` and
`Prototype_*` benchmarks remain benchmark-only comparisons. Malformed-input
benchmarks are named to distinguish cheap predicate validation from
exception-path cost. Predicate benchmarks do not measure exception allocation;
`ExceptionPath` benchmarks do.

Small predicate benchmarks use a longer BenchmarkDotNet minimum iteration time
to avoid unstable sub-100ms measurement iterations.

Use benchmark category filters as the primary quality control. These benchmark
groups are tagged with `[BenchmarkCategory]`; use BenchmarkDotNet's
`--anyCategories` option for the staged runs.

- quick checks: one filtered run to confirm benchmark compilation and basic shape
- rigorous checks: three independent filtered runs for decision-making
- deep checks: three independent runs of only a small suspicious shortlist, such
  as duplicate registration, duplicate-key equality/hash, extraNonce2
  canonicalization, or end-to-end submit validation

Do not apply deep timing to the entire suite by default. Very small nanosecond
benchmarks are sensitive to turbo, thermal drift, process scheduling, and
BenchmarkDotNet distribution warnings; compare medians, means, confidence
intervals, standard deviation, and allocated bytes/op across independent runs.

The benchmark project references the Release `HashStormCore.dll` output instead
of using a project reference. Build the solution in Release before running
benchmarks. This avoids forcing BenchmarkDotNet's generated harness to rebuild
the native mining libraries.

Duplicate-key prototype benchmarks are benchmark-only experiments. They compare
the historical string-key shape, the current production structured key, and
alternative structured candidates. Prototype keys are not production behavior
and must not be treated as a semantic change.

Current duplicate-key benchmark groups:

- current string-key equivalent: construct, lookup, and construct+lookup
- nullable structured prototype: `uint? versionBits`
- custom structured prototype: `bool hasVersionBits` plus `uint versionBits`
- production structured duplicate key: construction, lookup, construct+lookup,
  equality, and hash-code cost
- production duplicate registration: `ConcurrentDictionary<BitcoinSubmitDuplicateKey, bool>`
  `TryAdd` hit/miss paths with small, medium, and large prepopulated sets where
  practical
- extraNonce2 validation and canonicalization: lowercase no-allocation,
  uppercase allocation, mixed-case allocation, validate-only, invalid non-hex,
  and invalid length paths
- production submit-input validation: legacy and version-rolling validation,
  duplicate-key construction, duplicate registration, and malformed-input
  exception paths

The structured prototypes include setup-time sanity checks for equality and hash
code semantics, including version-bit differences, legacy no-version-bit keys,
nonce differences, and case-sensitive extranonce strings. These checks are only
there to keep the benchmark prototypes honest; they are not production tests.

Some miss-path duplicate registration benchmarks are batch benchmarks with
`OperationsPerInvoke` because a single `TryAdd` miss mutates the dictionary.
Batch methods clear and refill a preallocated dictionary inside the benchmark and
report amortized cost per registration. Duplicate hit-path benchmarks can use
prepopulated dictionaries directly because failed `TryAdd` calls do not mutate
the collection.

Benchmarks named `TryAdd_*` use prebuilt keys and isolate dictionary insertion
or lookup behavior. They intentionally exclude key-construction cost. Use
`ConstructAndTryAdd_*` when comparing the old string-key path against the
production structured-key path, because production has to construct the
duplicate key before registration.

Benchmarks named `ResetPerOperation` clear the benchmark dictionary inside every
measured operation. They are useful as stress/isolation measurements, but they
include dictionary reset and first-insert costs and should not be treated as the
steady production registration path. Prefer batch benchmarks with
`OperationsPerInvoke` for miss-path allocation and timing decisions.

Native hashing benchmarks are intentionally not included yet. Add them only when
they can call an existing HashStormCore wrapper and use deterministic correctness
fixtures already present in the repository.

Event-pipeline benchmarks are intentionally not included yet. Add them only for
pure in-memory mapping, serialization, or handoff preparation paths that do not
touch Redis, PostgreSQL, WAL/fsync, network I/O, or daemons.

To compare native C++14 and C++17 builds, run the same benchmark command on each
commit or branch and compare the BenchmarkDotNet results under
`BenchmarkDotNet.Artifacts`.

Optimization candidates should be treated as hypotheses until backed by both
correctness fixtures and before/after benchmark results. Current likely areas to
investigate next are duplicate-key allocation, canonical hex string allocation,
exception-path cost for malformed submits, native wrapper buffer copies, and
pure event serialization/mapping allocation.
