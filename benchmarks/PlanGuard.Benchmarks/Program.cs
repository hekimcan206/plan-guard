using BenchmarkDotNet.Running;
using PlanGuard.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(PlanBenchmarks).Assembly).Run(args);
