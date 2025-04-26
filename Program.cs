using Microsoft.AspNetCore.SignalR;
using RouteOptimizationApi.Common;
using RouteOptimizationApi.Hubs;
using RouteOptimizationApi.Models;
using RouteOptimizationApi.Services;
using System.Diagnostics;

namespace RouteOptimizationApi;

/// <summary>
/// Contains the main entry point for the application and configures endpoints for route optimization.
/// </summary>
public class Program
{
    /// <summary>
    /// Sets up and runs the web application, providing route optimization functionality.
    /// </summary>
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSignalR();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowNextApp", policy =>
            {
                policy.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            });
        });

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseDeveloperExceptionPage();
        }

        app.UseCors("AllowNextApp");
        app.MapHub<OptimizationHub>("/optimizationhub");

        /// <summary>
        /// Main optimization endpoint. Accepts an OptimizationRequest and returns an OptimizationResult.
        /// </summary>
        app.MapPost("/api/optimize", async (OptimizationRequest request, IHostApplicationLifetime lifetime, IHubContext<OptimizationHub> hubContext) =>
        {
            Stopwatch overallStopwatch = Stopwatch.StartNew();
            OptimizationResult nearestNeighborResult = null;
            OptimizationResult clarkeWrightResult = null;
            OptimizationResult finalCombinedResult = new();

            async Task SendProgress(string step, string msg, string style, object data = null, bool clear = false)
            {
                await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate(step, msg, style, data ?? new { }, clear));
            }

            try
            {
                await SendProgress("INIT", "Starting Optimization Process...", "header");
                await SendProgress("SETUP", "Scenario: " + request.NumberOfDeliveries + " deliveries, " + request.NumberOfDrivers + " drivers.", "info");
                await SendProgress("SETUP", "Coordinate Range: [" + request.MinCoordinate + ", " + request.MaxCoordinate + "]", "info");
                await SendProgress("SETUP", "Depot: " + TspAlgorithm.Depot, "info");
                await SendProgress("SETUP", "Heuristics: Nearest Neighbor, Clarke-Wright Savings", "info");
                await SendProgress("SETUP", "Optimization: 2-Opt", "info");
                await SendProgress("SETUP", "Partitioning Goal: Minimize Makespan (Binary Search)", "info");

                ValidateRequest(request, hubContext);

                await SendProgress("GENERATE", "[1. GENERATING DELIVERIES...]", "step-header");
                Stopwatch generationTimer = Stopwatch.StartNew();
                List<Delivery> allDeliveries = TspAlgorithm.GenerateRandomDeliveries(request.NumberOfDeliveries, request.MinCoordinate, request.MaxCoordinate);
                generationTimer.Stop();
                double generationMs = generationTimer.Elapsed.TotalMilliseconds;

                await SendProgress("GENERATE", "✔ " + allDeliveries.Count + " random deliveries generated.", "success", new { Time = FormatTimeSpan(generationTimer.Elapsed) });
                await SendProgress("GENERATE", "Time elapsed: " + FormatTimeSpan(generationTimer.Elapsed), "detail");

                nearestNeighborResult = await RunOptimizationPath("NN", TspAlgorithm.ConstructNearestNeighborRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                if (nearestNeighborResult != null)
                {
                    nearestNeighborResult.GenerationTimeMs = generationMs;
                }

                clarkeWrightResult = await RunOptimizationPath("CWS", TspAlgorithm.ConstructClarkeWrightRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                if (clarkeWrightResult != null)
                {
                    clarkeWrightResult.GenerationTimeMs = generationMs;
                }

                await SendProgress("COMPARE", "[3. COMPARISON & FINAL RESULTS]", "step-header");

                if (nearestNeighborResult == null || clarkeWrightResult == null)
                {
                    throw new InvalidOperationException("Either NN or CWS path data is null.");
                }

                bool nnIsBetter = nearestNeighborResult.MinMakespan < clarkeWrightResult.MinMakespan;
                bool resultsTie = Math.Abs(nearestNeighborResult.MinMakespan - clarkeWrightResult.MinMakespan) < Constants.Epsilon;
                OptimizationResult bestPathResult = resultsTie || nnIsBetter ? nearestNeighborResult : clarkeWrightResult;
                string bestMethod = resultsTie ? "NN / CWS (Tie)" : nnIsBetter ? "Nearest Neighbor" : "Clarke-Wright Savings";

                await SendProgress("COMPARE", "Comparison of Final Makespan:", "info");
                await SendProgress("COMPARE", "- Nearest Neighbor : " + nearestNeighborResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(nearestNeighborResult.PathExecutionTimeMs)) });
                await SendProgress("COMPARE", "- Clarke-Wright   : " + clarkeWrightResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(clarkeWrightResult.PathExecutionTimeMs)) });
                await SendProgress("COMPARE", "✔ Best Result: '" + bestMethod + "' -> Makespan: " + bestPathResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "success");

                finalCombinedResult = bestPathResult;
                finalCombinedResult.BestMethod = bestMethod;

                List<Delivery> combinedDeliveries = [TspAlgorithm.Depot, .. allDeliveries];
                finalCombinedResult.GeneratedDeliveries = combinedDeliveries;

                finalCombinedResult.InitialDistanceNN = nearestNeighborResult.InitialDistanceNN;
                finalCombinedResult.OptimizedDistanceNN = nearestNeighborResult.OptimizedDistanceNN;
                finalCombinedResult.InitialDistanceCWS = clarkeWrightResult.InitialDistanceCWS;
                finalCombinedResult.OptimizedDistanceCWS = clarkeWrightResult.OptimizedDistanceCWS;
                finalCombinedResult.PathExecutionTimeMs = bestPathResult.PathExecutionTimeMs;

                await SendProgress("DETAILS", "[4. DETAILS OF BEST SOLUTION (" + bestMethod + ")]", "step-header");
                await SendProgress("DETAILS", "Optimized Route Order (Delivery IDs):", "info");
                IEnumerable<int> routeIds = finalCombinedResult.OptimizedRoute.Any() ? finalCombinedResult.OptimizedRoute.Select(d => d.Id) : Enumerable.Empty<int>();
                string routeChain = CreateRouteChainString(routeIds, finalCombinedResult.OptimizedRoute.Count);

                await SendProgress("DETAILS", routeChain, "detail");

                if (finalCombinedResult.OptimizedRoute.Any())
                {
                    finalCombinedResult.DriverRoutes = PopulateDriverRoutes(finalCombinedResult.OptimizedRoute, finalCombinedResult.BestCutIndices, request.NumberOfDrivers);
                    await SendProgress("DRIVERS", "[DRIVER SUB-ROUTE DETAILS]", "step-header");

                    foreach (DriverRoute driverRoute in finalCombinedResult.DriverRoutes)
                    {
                        await SendProgress("DRIVERS", "Driver #" + driverRoute.DriverId + ":", "info");
                        if (driverRoute.OriginalIndices.Count > 2 && driverRoute.DeliveryIds.Count > 2)
                        {
                            int startIndex = driverRoute.OriginalIndices[1];
                            int endIndex = driverRoute.OriginalIndices[driverRoute.OriginalIndices.Count - 2];
                            await SendProgress("DRIVERS", "- Route Indices : [" + startIndex + ".." + endIndex + "]", "detail");
                            await SendProgress("DRIVERS", "- Total Distance: " + driverRoute.Distance.ToString("F2") + " " + Constants.DistanceUnit, "detail");
                            await SendProgress("DRIVERS", " Sub-Route IDs : " + string.Join(" -> ", driverRoute.DeliveryIds), "detail-mono");
                        }
                        else
                        {
                            await SendProgress("DRIVERS", "- Route Indices : N/A (No deliveries)", "detail");
                            await SendProgress("DRIVERS", "- Total Distance: " + driverRoute.Distance.ToString("F2") + " " + Constants.DistanceUnit, "detail");
                            await SendProgress("DRIVERS", " Sub-Route IDs : " + string.Join(" -> ", driverRoute.DeliveryIds), "detail-mono");
                        }
                    }
                }
                else
                {
                    finalCombinedResult.DriverRoutes = [];
                    await SendProgress("DRIVERS", "[No driver routes generated]", "warning");
                }

                overallStopwatch.Stop();
                finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;

                await SendProgress("SUMMARY", "[FINAL SUMMARY]", "header");
                await SendProgress("SUMMARY", "Scenario: " + request.NumberOfDeliveries + " deliveries, " + request.NumberOfDrivers + " drivers.", "info");
                await SendProgress("SUMMARY", "Best Makespan (" + bestMethod + "): " + finalCombinedResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "result");
                await SendProgress("SUMMARY", "Total Execution Time: " + FormatTimeSpan(overallStopwatch.Elapsed), "info");
                await SendProgress("END", "✔ Optimization Complete!", "success-large");
                return Results.Ok(finalCombinedResult);
            }
            catch (ArgumentException argEx)
            {
                overallStopwatch.Stop();
                finalCombinedResult.ErrorMessage = argEx.Message;
                finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                await SendProgress("ERROR", "Input Error: " + argEx.Message, "error");
                await SendProgress("END", "✖ Optimization Failed!", "error-large");
                return Results.BadRequest(finalCombinedResult);
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                finalCombinedResult.ErrorMessage = app.Environment.IsDevelopment() ? ex.ToString() : "An unexpected error occurred.";
                finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                await SendProgress("ERROR", "Internal Server Error: " + finalCombinedResult.ErrorMessage, "error");
                await SendProgress("END", "✖ Optimization Failed!", "error-large");
                return Results.Problem(detail: finalCombinedResult.ErrorMessage, statusCode: 500, title: "Optimization Failed");
            }
        })
        .WithName("OptimizeRoutes")
        .WithOpenApi()
        .Produces<OptimizationResult>(StatusCodes.Status200OK)
        .Produces<OptimizationResult>(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        await app.RunAsync();
    }

    /// <summary>
    /// Runs a specific route-building and optimization path, then partitions the optimized route among drivers.
    /// </summary>
    private static async Task<OptimizationResult> RunOptimizationPath(
        string algorithmLabel,
        Func<List<Delivery>, List<Delivery>> routeBuilder,
        List<Delivery> allDeliveries,
        int numberOfDrivers,
        IHubContext<OptimizationHub> hubContext
    )
    {
        OptimizationResult pathResult = new();
        Stopwatch totalPathWatch = Stopwatch.StartNew();
        string fullMethodName = algorithmLabel == "NN" ? "Nearest Neighbor" : "Clarke-Wright Savings";

        async Task SendProgress(string step, string msg, string style, object data = null, bool clear = false)
        {
            await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate(step, msg, style, data ?? new { }, clear));
        }

        await SendProgress(algorithmLabel, "===== PATH " + algorithmLabel + ": " + fullMethodName + " =====", "header");

        Stopwatch buildSw = Stopwatch.StartNew();

        // this line build the inital route, you shuold pass to it the function that is doing that specific algorithem!
        List<Delivery> initialRoute = routeBuilder(allDeliveries);
        buildSw.Stop();
        pathResult.BuildTimeMs = buildSw.Elapsed.TotalMilliseconds;

        double initialDistance = TspAlgorithm.ComputeTotalRouteDistance(initialRoute);
        if (algorithmLabel == "NN")
        {
            pathResult.InitialDistanceNN = initialDistance;
        }
        else
        {
            pathResult.InitialDistanceCWS = initialDistance;
        }

        await SendProgress(algorithmLabel + ".1", "✔ " + algorithmLabel + " initial TSP route constructed.", "success", new { Time = FormatTimeSpan(buildSw.Elapsed) });
        await SendProgress(algorithmLabel + ".1", "* " + algorithmLabel + " Initial Route Distance: " + initialDistance.ToString("F2") + " " + Constants.DistanceUnit, "detail");

        Stopwatch optSw = Stopwatch.StartNew();
        await SendProgress(algorithmLabel + ".2", "[" + algorithmLabel + ".2 Optimizing Route (2-Opt)...]", "step");
        List<Delivery> optimizedRoute = TspAlgorithm.OptimizeRouteUsing2Opt(initialRoute);
        optSw.Stop();
        pathResult.OptimizeTimeMs = optSw.Elapsed.TotalMilliseconds;

        double optimizedDistance = TspAlgorithm.ComputeTotalRouteDistance(optimizedRoute);
        if (algorithmLabel == "NN")
        {
            pathResult.OptimizedDistanceNN = optimizedDistance;
        }
        else
        {
            pathResult.OptimizedDistanceCWS = optimizedDistance;
        }

        double distanceImprovement = initialDistance - optimizedDistance;
        double improvementPercentage = initialDistance > Constants.Epsilon
            ? distanceImprovement / initialDistance * 100.0
            : 0;

        await SendProgress(algorithmLabel + ".2", "✔ " + algorithmLabel + " route optimized.", "success", new { Time = FormatTimeSpan(optSw.Elapsed) });
        await SendProgress(algorithmLabel + ".2", "* " + algorithmLabel + " Optimized Route Distance: " + optimizedDistance.ToString("F2") + " " + Constants.DistanceUnit, "detail");
        await SendProgress(algorithmLabel + ".2", "* Improvement Achieved: " + distanceImprovement.ToString("F2") + " (" + improvementPercentage.ToString("F2") + "%)", "detail");

        pathResult.OptimizedRoute = optimizedRoute;

        Stopwatch partSw = Stopwatch.StartNew();
        await SendProgress(algorithmLabel + ".3", "[" + algorithmLabel + ".3 Partitioning for " + numberOfDrivers + " Drivers (Binary Search)...]", "step");
        long iterations = 0;
        Stopwatch progressReportSw = Stopwatch.StartNew();

        TspAlgorithm.ProgressReporter progressCallback = count =>
        {
            iterations = count;
            if (progressReportSw.ElapsedMilliseconds > Constants.ProgressReportIntervalMs)
            {
                _ = SendProgress(algorithmLabel + ".3", "Partitioning (Binary Search Iteration: " + iterations + ")...", "progress", null, true);
                progressReportSw.Restart();
            }
        };

        TspAlgorithm.FindBestPartitionBinarySearch(
            optimizedRoute,
            numberOfDrivers,
            progressCallback,
            out int[] bestCuts,
            out double minMakespan
        );
        partSw.Stop();
        pathResult.PartitionTimeMs = partSw.Elapsed.TotalMilliseconds;

        await SendProgress(algorithmLabel + ".3", "Partitioning complete after " + iterations + " iterations.", "progress", null, true);

        pathResult.BestCutIndices = bestCuts;
        pathResult.MinMakespan = minMakespan;

        await SendProgress(algorithmLabel + ".3", "✔ Optimal partitioning for " + algorithmLabel + " route found.", "success", new { Time = FormatTimeSpan(partSw.Elapsed) });
        await SendProgress(algorithmLabel + ".3", "* Optimal Cut Indices: [" + string.Join(", ", pathResult.BestCutIndices) + "]", "detail");
        await SendProgress(algorithmLabel + ".3", "* Minimum Makespan: " + minMakespan.ToString("F2") + " " + Constants.DistanceUnit, "result");

        totalPathWatch.Stop();
        pathResult.PathExecutionTimeMs = totalPathWatch.Elapsed.TotalMilliseconds;
        await SendProgress(algorithmLabel, "Total time for " + algorithmLabel + " path: " + FormatTimeSpan(totalPathWatch.Elapsed), "info");

        return pathResult;
    }

    /// <summary>
    /// Breaks the final optimized route into individual driver routes using the best partition cut indices.
    /// </summary>
    private static List<DriverRoute> PopulateDriverRoutes(List<Delivery> optimizedRoute, int[] cutIndices, int driverCount)
    {
        List<DriverRoute> driverRoutes = [];
        if (optimizedRoute == null || optimizedRoute.Count < 2) return driverRoutes;

        int deliveryCountInRoute = optimizedRoute.Count - 2;
        if (deliveryCountInRoute <= 0)
        {
            AddEmptyRoutesForAllDrivers(driverCount, driverRoutes, optimizedRoute.Count);
            return driverRoutes;
        }

        int[] effectiveCuts = cutIndices ?? Array.Empty<int>();
        int currentIndex = 0;

        for (int driverIndex = 1; driverIndex <= driverCount; driverIndex++)
        {
            DriverRoute driverRoute = new() { DriverId = driverIndex };

            int routeStart = currentIndex + 1;
            int routeEnd = (driverIndex - 1) < effectiveCuts.Length
                ? effectiveCuts[driverIndex - 1]
                : deliveryCountInRoute;

            driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
            driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
            driverRoute.OriginalIndices.Add(0);

            bool hasDeliveries = routeStart <= routeEnd && routeStart >= 1 && routeEnd <= deliveryCountInRoute;
            if (hasDeliveries)
            {
                for (int deliveryIndex = routeStart; deliveryIndex <= routeEnd; deliveryIndex++)
                {
                    if (deliveryIndex > 0 && deliveryIndex < optimizedRoute.Count - 1)
                    {
                        driverRoute.RoutePoints.Add(optimizedRoute[deliveryIndex]);
                        driverRoute.DeliveryIds.Add(optimizedRoute[deliveryIndex].Id);
                        driverRoute.OriginalIndices.Add(deliveryIndex);
                    }
                }
            }

            driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
            driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
            driverRoute.OriginalIndices.Add(optimizedRoute.Count - 1);

            driverRoute.Distance = TspAlgorithm.ComputeSubRouteDistanceWithDepot(optimizedRoute, routeStart, routeEnd);
            driverRoutes.Add(driverRoute);

            currentIndex = routeEnd;
            if (currentIndex >= deliveryCountInRoute && driverIndex < driverCount)
            {
                AddEmptyRoutesForRemainingDrivers(driverIndex, driverCount, driverRoutes, optimizedRoute.Count);
                break;
            }
        }

        while (driverRoutes.Count < driverCount)
        {
            DriverRoute emptyDriverRoute = CreateEmptyDriverRoute(driverRoutes.Count + 1, optimizedRoute.Count);
            driverRoutes.Add(emptyDriverRoute);
        }

        return driverRoutes;
    }

    /// <summary>
    /// Validates basic request constraints and sends warnings if they are not met.
    /// </summary>
    private static void ValidateRequest(OptimizationRequest request, IHubContext<OptimizationHub> hubContext)
    {
        if (request.NumberOfDeliveries <= 0 || request.NumberOfDrivers <= 0)
        {
            throw new ArgumentException("Number of deliveries and drivers must be positive.");
        }
        if (request.MinCoordinate >= request.MaxCoordinate)
        {
            throw new ArgumentException("Min coordinate must be less than Max coordinate.");
        }
        if (request.NumberOfDeliveries < request.NumberOfDrivers)
        {
            _ = hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate(
                "SETUP",
                "Warning: Fewer deliveries (" + request.NumberOfDeliveries + ") than drivers (" + request.NumberOfDrivers + "). Some drivers may have no route.",
                "warning",
                null
            ));
        }
    }

    /// <summary>
    /// Constructs a shorter or truncated route chain display for progress messages.
    /// </summary>
    private static string CreateRouteChainString(IEnumerable<int> routeIds, int routeCount)
    {
        if (routeCount > Constants.MaxRouteDisplay + 2)
        {
            return string.Join(" -> ", routeIds.Take(Constants.MaxRouteDisplay / 2))
                + " ... "
                + string.Join(" -> ", routeIds.Skip(routeCount - Constants.MaxRouteDisplay / 2));
        }
        else
        {
            return string.Join(" -> ", routeIds);
        }
    }

    /// <summary>
    /// Adds empty route entries for all drivers when no deliveries exist.
    /// </summary>
    private static void AddEmptyRoutesForAllDrivers(int driverCount, List<DriverRoute> driverRoutes, int routeLength)
    {
        for (int d = 1; d <= driverCount; d++)
        {
            DriverRoute empty = CreateEmptyDriverRoute(d, routeLength);
            driverRoutes.Add(empty);
        }
    }

    /// <summary>
    /// Adds empty route entries for any remaining drivers if the route has been fully partitioned.
    /// </summary>
    private static void AddEmptyRoutesForRemainingDrivers(int currentDriver, int driverCount, List<DriverRoute> driverRoutes, int routeLength)
    {
        for (int d = currentDriver + 1; d <= driverCount; d++)
        {
            DriverRoute empty = CreateEmptyDriverRoute(d, routeLength);
            driverRoutes.Add(empty);
        }
    }

    /// <summary>
    /// Creates an empty driver route object with only depot entries.
    /// </summary>
    private static DriverRoute CreateEmptyDriverRoute(int driverId, int routeLength)
    {
        DriverRoute empty = new() { DriverId = driverId };
        empty.RoutePoints.Add(TspAlgorithm.Depot);
        empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
        empty.OriginalIndices.Add(0);

        empty.RoutePoints.Add(TspAlgorithm.Depot);
        empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
        empty.OriginalIndices.Add(routeLength > 0 ? routeLength - 1 : 0);

        return empty;
    }

    /// <summary>
    /// Turns a TimeSpan into a short string, like "0.12 ms" or "1.234 sec".
    /// </summary>
    public static string FormatTimeSpan(TimeSpan span)
    {
        double ms = span.TotalMilliseconds;
        if (ms >= 1000) return (ms / 1000.0).ToString("F3") + " sec";
        if (ms < 1.0 && ms > 0) return (ms * 1000.0).ToString("F0") + " µs";
        if (ms == 0) return "0 ms";
        return ms.ToString("F2") + " ms";
    }
}
