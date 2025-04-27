namespace RouteOptimizationApi.Models;

public class OptimizationRequest
{
    public int NumberOfDeliveries { get; set; }
    public int NumberOfDrivers { get; set; }
    public int MinCoordinate { get; set; } = -10000;
    public int MaxCoordinate { get; set; } = 10000;
}

public class OptimizationResult
{
    public string BestMethod { get; set; } = string.Empty;
    public List<Delivery> GeneratedDeliveries { get; set; } = [];
    public List<Delivery> OptimizedRoute { get; set; } = [];
    public int[] BestCutIndices { get; set; } = [];
    public double MinMakespan { get; set; }
    public List<DriverRoute> DriverRoutes { get; set; } = [];
    public double InitialDistanceNN { get; set; }
    public double OptimizedDistanceNN { get; set; }
    public double InitialDistanceCWS { get; set; }
    public double OptimizedDistanceCWS { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public double TotalExecutionTimeMs { get; set; }
    public double PathExecutionTimeMs { get; set; }
    public double GenerationTimeMs { get; set; }
    public double BuildTimeMs { get; set; }
    public double OptimizeTimeMs { get; set; }
    public double PartitionTimeMs { get; set; }
}


public class AlgorithmBenchmark
{
    public string HeuristicName { get; set; } = string.Empty;

    public double InitialRouteBuildTimeMs { get; set; }
    public double RouteOptimizationTimeMs { get; set; }
    public double RoutePartitioningTimeMs { get; set; }
    public double PartitioningTimeMs { get; set; }

    public double InitialRouteDistance { get; set; }
    public double OptimizedRouteDistance { get; set; }

    public double ImprovementPercentage { get; set; }

    public double Makespan { get; set; }

}
