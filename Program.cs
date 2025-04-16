using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace RouteOptimizationApi
{
    public class OptimizationRequest
    {
        public int NumberOfDeliveries { get; set; }
        public int NumberOfDrivers { get; set; }
        public int MinCoordinate { get; set; } = -10000;
        public int MaxCoordinate { get; set; } = 10000;
    }
    public class DriverRoute
    {
        public int DriverId { get; set; }
        public List<Delivery> RoutePoints { get; set; } = new();
        public List<int> DeliveryIds { get; set; } = new();
        public double Distance { get; set; }
        public List<int> OriginalIndices { get; set; } = new();
    }
    public class OptimizationResult
    {
        public string BestMethod { get; set; } = string.Empty;
        public List<Delivery> GeneratedDeliveries { get; set; } = new();
        public List<Delivery> OptimizedRoute { get; set; } = new();
        public int[] BestCutIndices { get; set; } = Array.Empty<int>();
        public double MinMakespan { get; set; }
        public List<DriverRoute> DriverRoutes { get; set; } = new();
        public double InitialDistanceNN { get; set; }
        public double OptimizedDistanceNN { get; set; }
        public double InitialDistanceCWS { get; set; }
        public double OptimizedDistanceCWS { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public double TotalExecutionTimeMs { get; set; }
        public double PathExecutionTimeMs { get; set; }
    }
    public class Delivery
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }
        public Delivery(int id, int x, int y) { Id = id; X = x; Y = y; }
        public override string ToString() => Id == 0 ? "Depot(0,0)" : $"Delivery#{Id}({X},{Y})";
    }
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSignalR();
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowNextApp", policy =>
                {
                    policy.WithOrigins("http://localhost:3000")
                          .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                });
            });

            var app = builder.Build();
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
                app.UseDeveloperExceptionPage();
            }
            app.UseCors("AllowNextApp");
            app.MapHub<OptimizationHub>("/optimizationhub");

            app.MapPost("/api/optimize", async (OptimizationRequest request,
                IHostApplicationLifetime lifetime,
                IHubContext<OptimizationHub> hubContext) =>
            {
                var overallStopwatch = Stopwatch.StartNew();
                OptimizationResult? nnResultData = null, cwsResultData = null;
                var finalCombinedResult = new OptimizationResult();

                // Local helper to send progress messages:
                async Task Log(string step, string msg, string style, object? data = null, bool clear = false)
                {
                    await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate(step, msg, style, data, clear));
                }

                try
                {
                    await Log("INIT", "Starting Optimization Process...", "header");
                    await Log("SETUP", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await Log("SETUP", $"Coordinate Range: [{request.MinCoordinate}, {request.MaxCoordinate}]", "info");
                    await Log("SETUP", $"Depot: {TspAlgorithm.Depot}", "info");
                    await Log("SETUP", "Heuristics: Nearest Neighbor, Clarke-Wright Savings", "info");
                    await Log("SETUP", "Optimization: 2-Opt", "info");
                    await Log("SETUP", "Partitioning Goal: Minimize Makespan (Binary Search)", "info");

                    if (request.NumberOfDeliveries <= 0 || request.NumberOfDrivers <= 0)
                        throw new ArgumentException("Number of deliveries and drivers must be positive.");
                    if (request.MinCoordinate >= request.MaxCoordinate)
                        throw new ArgumentException("Min coordinate must be less than Max coordinate.");
                    if (request.NumberOfDeliveries < request.NumberOfDrivers)
                        await Log("SETUP",
                            $"Warning: Fewer deliveries ({request.NumberOfDeliveries}) than drivers ({request.NumberOfDrivers}). Some drivers may have no route.",
                            "warning");

                    await Log("GENERATE", "[1. GENERATING DELIVERIES...]", "step-header");
                    var genWatch = Stopwatch.StartNew();
                    var allDeliveries = TspAlgorithm.GenerateRandomDeliveries(
                        request.NumberOfDeliveries, request.MinCoordinate, request.MaxCoordinate);
                    genWatch.Stop();

                    await Log("GENERATE", $"✔ {allDeliveries.Count} random deliveries generated.", "success", new { Time = FormatTimeSpan(genWatch.Elapsed) });
                    await Log("GENERATE", $"Time elapsed: {FormatTimeSpan(genWatch.Elapsed)}", "detail");

                    nnResultData = await RunOptimizationPath("NN", TspAlgorithm.ConstructNearestNeighborRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    cwsResultData = await RunOptimizationPath("CWS", TspAlgorithm.ConstructClarkeWrightRoute, allDeliveries, request.NumberOfDrivers, hubContext);

                    await Log("COMPARE", "[3. COMPARISON & FINAL RESULTS]", "step-header");
                    if (nnResultData == null || cwsResultData == null)
                        throw new InvalidOperationException("Either NN or CWS path data is null.");

                    bool nnBetter = nnResultData.MinMakespan < cwsResultData.MinMakespan;
                    bool tie = Math.Abs(nnResultData.MinMakespan - cwsResultData.MinMakespan) < Constants.Epsilon;
                    var bestPathResult = tie || nnBetter ? nnResultData : cwsResultData;
                    var winnerMethod = tie ? "NN / CWS (Tie)" : nnBetter ? "Nearest Neighbor" : "Clarke-Wright Savings";

                    await Log("COMPARE", "Comparison of Final Makespan:", "info");
                    await Log("COMPARE", $"- Nearest Neighbor : {nnResultData.MinMakespan:F2} {Constants.DistanceUnit}", "detail",
                        new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(nnResultData.PathExecutionTimeMs)) });
                    await Log("COMPARE", $"- Clarke-Wright   : {cwsResultData.MinMakespan:F2} {Constants.DistanceUnit}", "detail",
                        new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(cwsResultData.PathExecutionTimeMs)) });
                    await Log("COMPARE", $"✔ Best Result: '{winnerMethod}' -> Makespan: {bestPathResult.MinMakespan:F2} {Constants.DistanceUnit}", "success");

                    finalCombinedResult = bestPathResult;
                    finalCombinedResult.BestMethod = winnerMethod;
                    var combinedDeliveries = new List<Delivery> { TspAlgorithm.Depot };
                    combinedDeliveries.AddRange(allDeliveries);
                    finalCombinedResult.GeneratedDeliveries = combinedDeliveries;
                    finalCombinedResult.InitialDistanceNN = nnResultData.InitialDistanceNN;
                    finalCombinedResult.OptimizedDistanceNN = nnResultData.OptimizedDistanceNN;
                    finalCombinedResult.InitialDistanceCWS = cwsResultData.InitialDistanceCWS;
                    finalCombinedResult.OptimizedDistanceCWS = cwsResultData.OptimizedDistanceCWS;
                    finalCombinedResult.PathExecutionTimeMs = bestPathResult.PathExecutionTimeMs;

                    await Log("DETAILS", $"[4. DETAILS OF BEST SOLUTION ({winnerMethod})]", "step-header");
                    await Log("DETAILS", "Optimized Route Order (Delivery IDs):", "info");
                    var ids = finalCombinedResult.OptimizedRoute.Any() ? finalCombinedResult.OptimizedRoute.Select(d => d.Id) : Enumerable.Empty<int>();

                    string routeChain;
                    if (finalCombinedResult.OptimizedRoute.Count > Constants.MaxRouteDisplay + 2)
                    {
                        routeChain = string.Join(" -> ", ids.Take(Constants.MaxRouteDisplay / 2))
                                     + " ... "
                                     + string.Join(" -> ", ids.Skip(finalCombinedResult.OptimizedRoute.Count - Constants.MaxRouteDisplay / 2));
                    }
                    else routeChain = string.Join(" -> ", ids);

                    await Log("DETAILS", routeChain, "detail");

                    if (finalCombinedResult.OptimizedRoute.Any())
                    {
                        finalCombinedResult.DriverRoutes = PopulateDriverRoutes(
                            finalCombinedResult.OptimizedRoute,
                            finalCombinedResult.BestCutIndices,
                            request.NumberOfDrivers
                        );
                        await Log("DRIVERS", "[DRIVER SUB-ROUTE DETAILS]", "step-header");
                        foreach (var dr in finalCombinedResult.DriverRoutes)
                        {
                            await Log("DRIVERS", $"Driver #{dr.DriverId}:", "info");
                            if (dr.OriginalIndices.Count > 2 && dr.DeliveryIds.Count > 2)
                            {
                                int startIdx = dr.OriginalIndices[1];
                                int endIdx = dr.OriginalIndices[^2];
                                await Log("DRIVERS", $"- Route Indices : [{startIdx}..{endIdx}]", "detail");
                                await Log("DRIVERS", $"- Total Distance: {dr.Distance:F2} {Constants.DistanceUnit}", "detail");
                                await Log("DRIVERS", $" Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
                            }
                            else
                            {
                                await Log("DRIVERS", "- Route Indices : N/A (No deliveries)", "detail");
                                await Log("DRIVERS", $"- Total Distance: {dr.Distance:F2} {Constants.DistanceUnit}", "detail");
                                await Log("DRIVERS", $" Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
                            }
                        }
                    }
                    else
                    {
                        finalCombinedResult.DriverRoutes = new();
                        await Log("DRIVERS", "[No driver routes generated]", "warning");
                    }

                    overallStopwatch.Stop();
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;

                    await Log("SUMMARY", "[FINAL SUMMARY]", "header");
                    await Log("SUMMARY", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await Log("SUMMARY", $"Best Makespan ({winnerMethod}): {finalCombinedResult.MinMakespan:F2} {Constants.DistanceUnit}", "result");
                    await Log("SUMMARY", $"Total Execution Time: {FormatTimeSpan(overallStopwatch.Elapsed)}", "info");
                    await Log("END", "✔ Optimization Complete!", "success-large");
                    return Results.Ok(finalCombinedResult);
                }
                catch (ArgumentException argEx)
                {
                    overallStopwatch.Stop();
                    finalCombinedResult.ErrorMessage = argEx.Message;
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await Log("ERROR", "Input Error: " + argEx.Message, "error");
                    await Log("END", "✖ Optimization Failed!", "error-large");
                    return Results.BadRequest(finalCombinedResult);
                }
                catch (Exception ex)
                {
                    overallStopwatch.Stop();
                    finalCombinedResult.ErrorMessage = app.Environment.IsDevelopment() ? ex.ToString() : "An unexpected error occurred.";
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await Log("ERROR", "Internal Server Error: " + finalCombinedResult.ErrorMessage, "error");
                    await Log("END", "✖ Optimization Failed!", "error-large");
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

        static async Task<OptimizationResult> RunOptimizationPath(
            string tag,
            Func<List<Delivery>, List<Delivery>> routeBuilder,
            List<Delivery> allDeliveries,
            int numberOfDrivers,
            IHubContext<OptimizationHub> hubContext
        )
        {
            var pathResult = new OptimizationResult();
            var totalPathWatch = Stopwatch.StartNew();

            string fullMethodName = tag == "NN" ? "Nearest Neighbor" : "Clarke-Wright Savings";
            async Task Log(string step, string msg, string style, object? data = null, bool clear = false)
            {
                await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate(step, msg, style, data, clear));
            }

            await Log(tag, $"===== PATH {tag}: {fullMethodName} =====", "header");

            var buildSw = Stopwatch.StartNew();
            var initialRoute = routeBuilder(allDeliveries);
            buildSw.Stop();

            double initialDist = TspAlgorithm.ComputeTotalRouteDistance(initialRoute);
            if (tag == "NN") pathResult.InitialDistanceNN = initialDist; else pathResult.InitialDistanceCWS = initialDist;

            await Log(tag + ".1", $"✔ {tag} initial TSP route constructed.", "success", new { Time = FormatTimeSpan(buildSw.Elapsed) });
            await Log(tag + ".1", $"* {tag} Initial Route Distance: {initialDist:F2} {Constants.DistanceUnit}", "detail");

            var optSw = Stopwatch.StartNew();
            await Log(tag + ".2", $"[{tag}.2 Optimizing Route (2-Opt)...]", "step");
            var optimizedRoute = TspAlgorithm.OptimizeRouteUsing2Opt(initialRoute);
            optSw.Stop();

            double optimizedDist = TspAlgorithm.ComputeTotalRouteDistance(optimizedRoute);
            if (tag == "NN") pathResult.OptimizedDistanceNN = optimizedDist; else pathResult.OptimizedDistanceCWS = optimizedDist;

            double improvement = initialDist - optimizedDist;
            double percent = initialDist > Constants.Epsilon ? (improvement / initialDist) * 100.0 : 0;

            await Log(tag + ".2", $"✔ {tag} route optimized.", "success", new { Time = FormatTimeSpan(optSw.Elapsed) });
            await Log(tag + ".2", $"* {tag} Optimized Route Distance: {optimizedDist:F2} {Constants.DistanceUnit}", "detail");
            await Log(tag + ".2", $"* Improvement Achieved: {improvement:F2} ({percent:F2}%)", "detail");

            pathResult.OptimizedRoute = optimizedRoute;

            var partSw = Stopwatch.StartNew();
            await Log(tag + ".3", $"[{tag}.3 Partitioning for {numberOfDrivers} Drivers (Binary Search)...]", "step");
            long iterations = 0;
            var progressReportSw = Stopwatch.StartNew();

            TspAlgorithm.ProgressReporter progressCallback = count =>
            {
                iterations = count;
                if (progressReportSw.ElapsedMilliseconds > Constants.ProgressReportIntervalMs)
                {
                    _ = Log(tag + ".3", $"Partitioning (Binary Search Iteration: {iterations})...", "progress", null, true);
                    progressReportSw.Restart();
                }
            };

            TspAlgorithm.FindBestPartitionBinarySearch(
                optimizedRoute,
                numberOfDrivers,
                progressCallback,
                out int[]? bestCuts,
                out double minMakespan
            );

            partSw.Stop();
            await Log(tag + ".3", $"Partitioning complete after {iterations} iterations.", "progress", null, true);

            pathResult.BestCutIndices = bestCuts ?? Array.Empty<int>();
            pathResult.MinMakespan = minMakespan;

            await Log(tag + ".3", $"✔ Optimal partitioning for {tag} route found.", "success", new { Time = FormatTimeSpan(partSw.Elapsed) });
            await Log(tag + ".3", $"* Optimal Cut Indices: [{string.Join(", ", pathResult.BestCutIndices)}]", "detail");
            await Log(tag + ".3", $"* Minimum Makespan: {minMakespan:F2} {Constants.DistanceUnit}", "result");

            totalPathWatch.Stop();
            pathResult.PathExecutionTimeMs = totalPathWatch.Elapsed.TotalMilliseconds;
            await Log(tag, $"Total time for {tag} path: {FormatTimeSpan(totalPathWatch.Elapsed)}", "info");

            return pathResult;
        }
        static List<DriverRoute> PopulateDriverRoutes(List<Delivery> optimizedRoute, int[] cutIndices, int driverCount)
        {
            var driverRoutes = new List<DriverRoute>();
            if (optimizedRoute == null || optimizedRoute.Count < 2) return driverRoutes;
            int deliveryCountInRoute = optimizedRoute.Count - 2;
            if (deliveryCountInRoute <= 0)
            {
                for (int d = 1; d <= driverCount; d++)
                {
                    var empty = new DriverRoute { DriverId = d };
                    empty.RoutePoints.Add(TspAlgorithm.Depot);
                    empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                    empty.OriginalIndices.Add(0);
                    empty.RoutePoints.Add(TspAlgorithm.Depot);
                    empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                    empty.OriginalIndices.Add(optimizedRoute.Count > 0 ? optimizedRoute.Count - 1 : 0);
                    driverRoutes.Add(empty);
                }
                return driverRoutes;
            }
            var effectiveCuts = cutIndices ?? Array.Empty<int>();
            int currentIndex = 0;
            for (int d = 1; d <= driverCount; d++)
            {
                var dr = new DriverRoute { DriverId = d };
                int routeStart = currentIndex + 1;
                int routeEnd = (d - 1) < effectiveCuts.Length ? effectiveCuts[d - 1] : deliveryCountInRoute;

                dr.RoutePoints.Add(TspAlgorithm.Depot);
                dr.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                dr.OriginalIndices.Add(0);

                bool hasDeliveries = routeStart <= routeEnd && routeStart >= 1 && routeEnd <= deliveryCountInRoute;
                if (hasDeliveries)
                {
                    for (int i = routeStart; i <= routeEnd; i++)
                    {
                        if (i > 0 && i < optimizedRoute.Count - 1)
                        {
                            dr.RoutePoints.Add(optimizedRoute[i]);
                            dr.DeliveryIds.Add(optimizedRoute[i].Id);
                            dr.OriginalIndices.Add(i);
                        }
                    }
                }
                dr.RoutePoints.Add(TspAlgorithm.Depot);
                dr.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                dr.OriginalIndices.Add(optimizedRoute.Count - 1);

                dr.Distance = TspAlgorithm.ComputeSubRouteDistanceWithDepot(optimizedRoute, routeStart, routeEnd);
                driverRoutes.Add(dr);
                currentIndex = routeEnd;
                if (currentIndex >= deliveryCountInRoute && d < driverCount)
                {
                    for (int r = d + 1; r <= driverCount; r++)
                    {
                        var empty = new DriverRoute { DriverId = r };
                        empty.RoutePoints.Add(TspAlgorithm.Depot);
                        empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                        empty.OriginalIndices.Add(0);
                        empty.RoutePoints.Add(TspAlgorithm.Depot);
                        empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                        empty.OriginalIndices.Add(optimizedRoute.Count - 1);
                        driverRoutes.Add(empty);
                    }
                    break;
                }
            }
            while (driverRoutes.Count < driverCount)
            {
                var empty = new DriverRoute { DriverId = driverRoutes.Count + 1 };
                empty.RoutePoints.Add(TspAlgorithm.Depot);
                empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                empty.OriginalIndices.Add(0);
                empty.RoutePoints.Add(TspAlgorithm.Depot);
                empty.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                empty.OriginalIndices.Add(optimizedRoute.Count - 1);
                driverRoutes.Add(empty);
            }
            return driverRoutes;
        }
        public static string FormatTimeSpan(TimeSpan span)
        {
            double ms = span.TotalMilliseconds;
            if (ms >= 1000) return (ms / 1000.0).ToString("F3") + " sec";
            if (ms < 1.0 && ms > 0) return (ms * 1000.0).ToString("F0") + " µs";
            if (ms == 0) return "0 ms";
            return ms.ToString("F2") + " ms";
        }
    }
}
