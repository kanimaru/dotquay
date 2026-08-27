using System.Runtime.CompilerServices;

// Lets Dotquay.Tests unit-test a handful of `internal` parsing routines (e.g. ProcessGitClient's
// `git ls-remote` output parser) directly with fabricated input, instead of needing a real network
// call to exercise the tricky bits (annotated-tag peeling in particular).
[assembly: InternalsVisibleTo("Dotquay.Tests")]
