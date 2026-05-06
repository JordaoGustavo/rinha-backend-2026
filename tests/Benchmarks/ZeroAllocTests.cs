using Xunit;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Benchmarks;

public class ZeroAllocTests
{
    [Fact]
    public void HotPath_ZeroAllocations_ZeroGC()
    {
        var config = ManualConfig.CreateMinimumViable()
            .AddJob(Job.Dry)
            .AddLogger(ConsoleLogger.Default)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        var summary = BenchmarkRunner.Run<HotPathBenchmarks>(config);

        Assert.False(summary.HasCriticalValidationErrors,
            "Benchmark validation errors: " + string.Join("; ", summary.ValidationErrors));
        Assert.NotEmpty(summary.Reports);

        foreach (var report in summary.Reports)
        {
            var name = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
            var gc = report.GcStats;
            long? allocated = gc.GetBytesAllocatedPerOperation(report.BenchmarkCase);

            Assert.True(allocated is null or 0,
                $"{name}: allocated {allocated} bytes");
            Assert.True(gc.Gen0Collections == 0,
                $"{name}: triggered {gc.Gen0Collections} Gen0 collections");
            Assert.True(gc.Gen1Collections == 0,
                $"{name}: triggered {gc.Gen1Collections} Gen1 collections");
            Assert.True(gc.Gen2Collections == 0,
                $"{name}: triggered {gc.Gen2Collections} Gen2 collections");
        }
    }
}
