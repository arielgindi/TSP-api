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
        public List<Delivery> RoutePoints { get; set; } = new List<Delivery>();
        public List<int> DeliveryIds { get; set; } = new List<int>();
        public double Distance { get; set; }
        public List<int> OriginalIndices { get; set; } = new List<int>();
    }

    public class OptimizationResult
    {
        public string BestMethod { get; set; } = string.Empty;
        public List<Delivery> GeneratedDeliveries { get; set; } = new List<Delivery>();
        public List<Delivery> OptimizedRoute { get; set; } = new List<Delivery>();
        public int[] BestCutIndices { get; set; } = Array.Empty<int>();
        public double MinMakespan { get; set; }
        public List<DriverRoute> DriverRoutes { get; set; } = new List<DriverRoute>();
        public double InitialDistanceNN { get; set; }
        public double OptimizedDistanceNN { get; set; }
        public double InitialDistanceGI { get; set; }
        public double OptimizedDistanceGI { get; set; }
        public long CombinationsCheckedNN { get; set; }
        public long CombinationsCheckedGI { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public double TotalExecutionTimeMs { get; set; }
        public double PathExecutionTimeMs { get; set; }
    }

    public class Delivery
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }

        public Delivery(int id, int x, int y)
        {
            Id = id;
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            if (Id == 0) return "Depot(0,0)";
            return "Delivery#" + Id + "(" + X + "," + Y + ")";
        }
    }

    public class Program
    {
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
                    policy.WithOrigins("http://localhost:3000")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
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

            app.MapPost("/api/optimize", async (OptimizationRequest request, IHostApplicationLifetime lifetime, IHubContext<OptimizationHub> hubContext) =>
            {
                Stopwatch overallStopwatch = new Stopwatch();
                overallStopwatch.Start();
                OptimizationResult? nnResultData = null;
                OptimizationResult? giResultData = null;
                OptimizationResult finalCombinedResult = new OptimizationResult();
                try
                {
                    await SendProgressUpdate(hubContext, "INIT", "Starting Optimization Process...", "header");
                    await SendProgressUpdate(hubContext, "SETUP", "Scenario: " + request.NumberOfDeliveries + " deliveries, " + request.NumberOfDrivers + " drivers.", "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Coordinate Range: [" + request.MinCoordinate + ", " + request.MaxCoordinate + "]", "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Depot: " + TspAlgorithm.Depot, "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Heuristics: Nearest Neighbor, Greedy Insertion", "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Optimization: 2-Opt", "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Partitioning Goal: Minimize Makespan (Brute-Force)", "info");
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
                        await SendProgressUpdate(hubContext, "SETUP", "Warning: Fewer deliveries (" + request.NumberOfDeliveries + ") than drivers (" + request.NumberOfDrivers + "). Some drivers may have no route.", "warning");
                    }
                    await SendProgressUpdate(hubContext, "GENERATE", "[1. GENERATING DELIVERIES...]", "step-header");
                    Stopwatch genWatch = new Stopwatch();
                    genWatch.Start();
                    List<Delivery> allDeliveries = TspAlgorithm.GenerateRandomDeliveries(request.NumberOfDeliveries, request.MinCoordinate, request.MaxCoordinate);
                    genWatch.Stop();
                    await SendProgressUpdate(hubContext, "GENERATE", "✔ " + allDeliveries.Count + " random deliveries generated successfully.", "success", new { Time = FormatTimeSpan(genWatch.Elapsed) });
                    await SendProgressUpdate(hubContext, "GENERATE", "Time elapsed: " + FormatTimeSpan(genWatch.Elapsed), "detail");
                    nnResultData = await RunOptimizationPath("NN", TspAlgorithm.ConstructNearestNeighborRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    giResultData = await RunOptimizationPath("GI", TspAlgorithm.ConstructGreedyInsertionRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    await SendProgressUpdate(hubContext, "COMPARE", "[3. COMPARISON & FINAL RESULTS]", "step-header");
                    bool nnBetter = nnResultData.MinMakespan < giResultData.MinMakespan;
                    bool tie = Math.Abs(nnResultData.MinMakespan - giResultData.MinMakespan) < Constants.Epsilon;
                    OptimizationResult bestPathResult;
                    string winnerMethod;
                    if (tie || nnBetter)
                    {
                        bestPathResult = nnResultData;
                        winnerMethod = tie ? "NN / GI (Tie)" : "Nearest Neighbor";
                    }
                    else
                    {
                        bestPathResult = giResultData;
                        winnerMethod = "Greedy Insertion";
                    }
                    await SendProgressUpdate(hubContext, "COMPARE", "Comparison of Final Makespan:", "info");
                    await SendProgressUpdate(hubContext, "COMPARE", "- Nearest Neighbor Path : " + nnResultData.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(nnResultData.PathExecutionTimeMs)) });
                    await SendProgressUpdate(hubContext, "COMPARE", "- Greedy Insertion Path : " + giResultData.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(giResultData.PathExecutionTimeMs)) });
                    await SendProgressUpdate(hubContext, "COMPARE", "✔ Best Result: '" + winnerMethod + "' -> Makespan: " + bestPathResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "success");
                    finalCombinedResult = bestPathResult;
                    finalCombinedResult.BestMethod = winnerMethod;
                    List<Delivery> combinedDeliveries = new List<Delivery> { TspAlgorithm.Depot };
                    combinedDeliveries.AddRange(allDeliveries);
                    finalCombinedResult.GeneratedDeliveries = combinedDeliveries;
                    finalCombinedResult.InitialDistanceNN = nnResultData.InitialDistanceNN;
                    finalCombinedResult.OptimizedDistanceNN = nnResultData.OptimizedDistanceNN;
                    finalCombinedResult.CombinationsCheckedNN = nnResultData.CombinationsCheckedNN;
                    finalCombinedResult.InitialDistanceGI = giResultData.InitialDistanceGI;
                    finalCombinedResult.OptimizedDistanceGI = giResultData.OptimizedDistanceGI;
                    finalCombinedResult.CombinationsCheckedGI = giResultData.CombinationsCheckedGI;
                    await SendProgressUpdate(hubContext, "DETAILS", "[4. DETAILS OF BEST SOLUTION (" + winnerMethod + ")]", "step-header");
                    await SendProgressUpdate(hubContext, "DETAILS", "Optimized Route Order (Delivery IDs):", "info");
                    IEnumerable<int> ids = finalCombinedResult.OptimizedRoute.Any() ? finalCombinedResult.OptimizedRoute.Select(d => d.Id) : Enumerable.Empty<int>();
                    string routeChain;
                    if (finalCombinedResult.OptimizedRoute.Count > Constants.MaxRouteDisplay + 2)
                    {
                        routeChain = string.Join(" -> ", ids.Take(Constants.MaxRouteDisplay / 2)) + " ... " + string.Join(" -> ", ids.Skip(finalCombinedResult.OptimizedRoute.Count - Constants.MaxRouteDisplay / 2));
                    }
                    else
                    {
                        routeChain = string.Join(" -> ", ids);
                    }
                    await SendProgressUpdate(hubContext, "DETAILS", routeChain, "detail");
                    if (finalCombinedResult.OptimizedRoute.Any())
                    {
                        finalCombinedResult.DriverRoutes = PopulateDriverRoutes(finalCombinedResult.OptimizedRoute, finalCombinedResult.BestCutIndices, request.NumberOfDrivers);
                        await SendProgressUpdate(hubContext, "DRIVERS", "[DRIVER SUB-ROUTE DETAILS]", "step-header");
                        foreach (DriverRoute dr in finalCombinedResult.DriverRoutes)
                        {
                            await SendProgressUpdate(hubContext, "DRIVERS", "Driver #" + dr.DriverId + ":", "info");
                            if (dr.OriginalIndices.Count > 2)
                            {
                                int startIdx = dr.OriginalIndices[1];
                                int endIdx = dr.OriginalIndices[dr.OriginalIndices.Count - 2];
                                await SendProgressUpdate(hubContext, "DRIVERS", "- Route Indices : [" + startIdx + ".." + endIdx + "]", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", "- Total Distance: " + dr.Distance.ToString("F2") + " " + Constants.DistanceUnit, "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", " Sub-Route IDs : " + string.Join(" -> ", dr.DeliveryIds), "detail-mono");
                            }
                            else
                            {
                                await SendProgressUpdate(hubContext, "DRIVERS", "- Route Indices : N/A", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", "- Total Distance: " + dr.Distance.ToString("F2") + " " + Constants.DistanceUnit, "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", " Sub-Route IDs : " + string.Join(" -> ", dr.DeliveryIds), "detail-mono");
                            }
                        }
                    }
                    else
                    {
                        finalCombinedResult.DriverRoutes = new List<DriverRoute>();
                        await SendProgressUpdate(hubContext, "DRIVERS", "[No driver routes generated]", "warning");
                    }
                    overallStopwatch.Stop();
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "SUMMARY", "[FINAL SUMMARY]", "header");
                    await SendProgressUpdate(hubContext, "SUMMARY", "Scenario: " + request.NumberOfDeliveries + " deliveries, " + request.NumberOfDrivers + " drivers.", "info");
                    await SendProgressUpdate(hubContext, "SUMMARY", "Best Makespan (" + winnerMethod + "): " + finalCombinedResult.MinMakespan.ToString("F2") + " " + Constants.DistanceUnit, "result");
                    await SendProgressUpdate(hubContext, "SUMMARY", "Total Execution Time: " + FormatTimeSpan(overallStopwatch.Elapsed), "info");
                    await SendProgressUpdate(hubContext, "END", "✔ Optimization Complete!", "success-large");
                    return Results.Ok(finalCombinedResult);
                }
                catch (ArgumentException argEx)
                {
                    overallStopwatch.Stop();
                    finalCombinedResult.ErrorMessage = argEx.Message;
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", "Input Error: " + argEx.Message, "error");
                    await SendProgressUpdate(hubContext, "END", "✖ Optimization Failed!", "error-large");
                    return Results.BadRequest(finalCombinedResult);
                }
                catch (Exception ex)
                {
                    overallStopwatch.Stop();
                    Console.WriteLine("Error during optimization: " + ex.Message + Environment.NewLine + "Stack Trace: " + ex.StackTrace);
                    finalCombinedResult.ErrorMessage = app.Environment.IsDevelopment() ? ex.ToString() : "An unexpected error occurred during optimization.";
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", "Internal Server Error: " + finalCombinedResult.ErrorMessage, "error");
                    await SendProgressUpdate(hubContext, "END", "✖ Optimization Failed!", "error-large");
                    return Results.Problem(
                        detail: finalCombinedResult.ErrorMessage,
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "Optimization Failed"
                    );
                }
            })
            .WithName("OptimizeRoutes")
            .WithOpenApi()
            .Produces<OptimizationResult>(StatusCodes.Status200OK)
            .Produces<OptimizationResult>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

            await app.RunAsync();
        }

        static async Task SendProgressUpdate(
            IHubContext<OptimizationHub> hubContext,
            string step,
            string message,
            string style,
            object? data = null,
            bool clearPrevious = false
        )
        {
            await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate
            {
                Step = step,
                Message = message,
                Style = style,
                Data = data,
                ClearPreviousProgress = clearPrevious
            });
        }

        static async Task<OptimizationResult> RunOptimizationPath(
            string tag,
            Func<List<Delivery>, List<Delivery>> routeBuilder,
            List<Delivery> allDeliveries,
            int numberOfDrivers,
            IHubContext<OptimizationHub> hubContext
        )
        {
            OptimizationResult pathResult = new OptimizationResult();
            Stopwatch totalPathWatch = new Stopwatch();
            totalPathWatch.Start();
            string fullMethodName = tag == "NN" ? "Nearest Neighbor" : "Greedy Insertion";
            await SendProgressUpdate(hubContext, tag, "===== PATH " + tag + ": " + fullMethodName + " =====", "header");
            Stopwatch buildSw = new Stopwatch();
            buildSw.Start();
            List<Delivery> initialRoute = routeBuilder(allDeliveries);
            buildSw.Stop();
            double initialDist = TspAlgorithm.ComputeTotalRouteDistance(initialRoute);
            if (tag == "NN") pathResult.InitialDistanceNN = initialDist;
            else pathResult.InitialDistanceGI = initialDist;
            await SendProgressUpdate(hubContext, tag + ".1", "✔ " + tag + " initial TSP route constructed.", "success", new { Time = FormatTimeSpan(buildSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".1", "* " + tag + " Initial Route Distance: " + initialDist.ToString("F2") + " " + Constants.DistanceUnit, "detail");
            Stopwatch optSw = new Stopwatch();
            optSw.Start();
            await SendProgressUpdate(hubContext, tag + ".2", "[" + tag + ".2 Optimizing Route (2-Opt)...]", "step");
            List<Delivery> optimizedRoute = TspAlgorithm.OptimizeRouteUsing2Opt(initialRoute);
            optSw.Stop();
            double optimizedDist = TspAlgorithm.ComputeTotalRouteDistance(optimizedRoute);
            if (tag == "NN") pathResult.OptimizedDistanceNN = optimizedDist;
            else pathResult.OptimizedDistanceGI = optimizedDist;
            double improvement = initialDist - optimizedDist;
            double percent = initialDist > Constants.Epsilon ? (improvement / initialDist * 100.0) : 0;
            await SendProgressUpdate(hubContext, tag + ".2", "✔ " + tag + " route optimized.", "success", new { Time = FormatTimeSpan(optSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".2", "* " + tag + " Optimized Route Distance: " + optimizedDist.ToString("F2") + " " + Constants.DistanceUnit, "detail");
            await SendProgressUpdate(hubContext, tag + ".2", "* Improvement Achieved: " + improvement.ToString("F2") + " " + Constants.DistanceUnit + " (" + percent.ToString("F2") + "%)", "detail");
            pathResult.OptimizedRoute = optimizedRoute;
            Stopwatch partSw = new Stopwatch();
            partSw.Start();
            await SendProgressUpdate(hubContext, tag + ".3", "[" + tag + ".3 Partitioning for " + numberOfDrivers + " Drivers...]", "step");
            long lastReportedCount = 0;
            Stopwatch progressReportSw = new Stopwatch();
            progressReportSw.Start();
            TspAlgorithm.ProgressReporter progressCallback = count =>
            {
                if (!progressReportSw.IsRunning || progressReportSw.ElapsedMilliseconds > Constants.ProgressReportIntervalMs)
                {
                    _ = SendProgressUpdate(hubContext, tag + ".3", "Checked " + count.ToString("N0") + " combinations...", "progress", null, true);
                    lastReportedCount = count;
                    progressReportSw.Restart();
                }
            };
            TspAlgorithm.FindBestPartition(
                optimizedRoute,
                numberOfDrivers,
                progressCallback,
                out int[]? bestCuts,
                out double minMakespan,
                out long combosChecked
            );
            partSw.Stop();
            await SendProgressUpdate(hubContext, tag + ".3", "Checked " + combosChecked.ToString("N0") + " combinations.", "progress", null, true);
            pathResult.BestCutIndices = bestCuts ?? Array.Empty<int>();
            pathResult.MinMakespan = minMakespan;
            if (tag == "NN") pathResult.CombinationsCheckedNN = combosChecked;
            else pathResult.CombinationsCheckedGI = combosChecked;
            await SendProgressUpdate(hubContext, tag + ".3", "✔ Optimal partitioning for " + tag + " route found.", "success", new { Time = FormatTimeSpan(partSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".3", "* Combinations Checked: " + combosChecked.ToString("N0"), "detail");
            await SendProgressUpdate(hubContext, tag + ".3", "* Optimal Cut Indices: [" + string.Join(", ", pathResult.BestCutIndices) + "]", "detail");
            await SendProgressUpdate(hubContext, tag + ".3", "* Minimum Makespan Achieved (" + tag + "): " + minMakespan.ToString("F2") + " " + Constants.DistanceUnit, "result");
            totalPathWatch.Stop();
            pathResult.PathExecutionTimeMs = totalPathWatch.Elapsed.TotalMilliseconds;
            await SendProgressUpdate(hubContext, tag, "Total time for " + tag + " path: " + FormatTimeSpan(totalPathWatch.Elapsed), "info");
            return pathResult;
        }

        static List<DriverRoute> PopulateDriverRoutes(List<Delivery> optimizedRoute, int[] cutIndices, int driverCount)
        {
            List<DriverRoute> driverRoutes = new List<DriverRoute>();
            if (optimizedRoute == null || optimizedRoute.Count < 2) return driverRoutes;
            int deliveryCountInRoute = optimizedRoute.Count - 2;
            int[] effectiveCuts;
            if (driverCount <= 1)
            {
                effectiveCuts = Array.Empty<int>();
            }
            else if (deliveryCountInRoute < driverCount)
            {
                effectiveCuts = new int[driverCount - 1];
                for (int i = 0; i < driverCount - 1; i++)
                {
                    effectiveCuts[i] = Math.Min(i + 1, deliveryCountInRoute > 0 ? deliveryCountInRoute : 1);
                }
                if (deliveryCountInRoute <= 0) effectiveCuts = Array.Empty<int>();
            }
            else if (cutIndices == null)
            {
                Console.WriteLine("Warning: PopulateDriverRoutes received null cutIndices unexpectedly.");
                effectiveCuts = Array.Empty<int>();
                driverCount = 1;
            }
            else
            {
                effectiveCuts = cutIndices;
            }
            int currentDeliveryRouteIndex = 0;
            for (int d = 1; d <= driverCount; d++)
            {
                DriverRoute driverRoute = new DriverRoute { DriverId = d };
                int routeStartIndex = currentDeliveryRouteIndex + 1;
                int routeEndIndex = d <= effectiveCuts.Length ? effectiveCuts[d - 1] : deliveryCountInRoute;
                bool routeHasDeliveries = routeStartIndex <= routeEndIndex && routeStartIndex >= 1 && routeEndIndex < optimizedRoute.Count - 1;
                driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                driverRoute.OriginalIndices.Add(0);
                if (routeHasDeliveries)
                {
                    for (int i = routeStartIndex; i <= routeEndIndex; i++)
                    {
                        if (i >= 0 && i < optimizedRoute.Count)
                        {
                            driverRoute.RoutePoints.Add(optimizedRoute[i]);
                            driverRoute.DeliveryIds.Add(optimizedRoute[i].Id);
                            driverRoute.OriginalIndices.Add(i);
                        }
                    }
                }
                driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                driverRoute.OriginalIndices.Add(optimizedRoute.Count > 0 ? optimizedRoute.Count - 1 : 0);
                driverRoute.Distance = TspAlgorithm.ComputeSubRouteDistanceWithDepot(optimizedRoute, routeStartIndex, routeEndIndex);
                driverRoutes.Add(driverRoute);
                currentDeliveryRouteIndex = routeEndIndex;
            }
            return driverRoutes;
        }

        public static string FormatTimeSpan(TimeSpan span)
        {
            double timeInMs = span.TotalMilliseconds;
            if (timeInMs >= 1000) return (timeInMs / 1000.0).ToString("F3") + " sec";
            if (timeInMs < 1.0 && timeInMs > 0) return (timeInMs * 1000.0).ToString("F0") + " µs";
            if (timeInMs == 0) return "0 ms";
            return timeInMs.ToString("F2") + " ms";
        }
    }
}
