using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Tests that measure allocations or time run alone: the threads of other tests take arrays from the shared pools, and
/// their garbage collections stall every thread and, under high memory load, empty those pools.
/// </summary>
[CollectionDefinition("Measurements", DisableParallelization = true)]
public class MeasurementsCollection { }
