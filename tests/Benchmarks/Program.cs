using BenchmarkDotNet.Running;
using Benchmarks;

BenchmarkRunner.Run<HotPathBenchmarks>(args: args);
