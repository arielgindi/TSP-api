// File: Program.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteOptimizationApi;

namespace RouteOptimizationApi
{
    // --- DTOs and Data Structures (Keep these as they are) ---
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
        public int[]? BestCutIndices { get; set; }
        public double MinMakespan { get; set; }
        public List<DriverRoute> DriverRoutes { get; set; } = new List<DriverRoute>();
        public double InitialDistanceNN { get; set; }
        public double OptimizedDistanceNN { get; set; }
        public double InitialDistanceGI { get; set; }
        public double OptimizedDistanceGI { get; set; }
        public long CombinationsCheckedNN { get; set; }
        public long CombinationsCheckedGI { get; set; }
        public string? ErrorMessage { get; set; }
        public double TotalExecutionTimeMs { get; set; }
        public double PathExecutionTimeMs { get; set; } // Added to store individual path time within the result object itself
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
            return Id == 0 ? "Depot(0,0)" : $"Delivery#{Id}({X},{Y})";
        }
    }

    // --- Main Program ---
    public class Program
    {
        private const string DISTANCE_UNIT = "d.u.";

        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSignalR();

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowNextApp",
                    policy =>
                    {
                        policy.WithOrigins("http://localhost:3000")
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
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

            app.MapPost("/api/optimize", async (
                OptimizationRequest request,
                IHostApplicationLifetime lifetime,
                IHubContext<OptimizationHub> hubContext) =>
            {
                Stopwatch overallStopwatch = Stopwatch.StartNew();
                // We'll create the final result shell later, after getting path results
                OptimizationResult? nnResultData = null;
                OptimizationResult? giResultData = null;
                OptimizationResult finalCombinedResult = new OptimizationResult(); // Used for returning errors or final combined data

                try
                {
                    await SendProgressUpdate(hubContext, "INIT", "Starting Optimization Process...", "header");
                    await SendProgressUpdate(hubContext, "SETUP", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Coordinate Range: [{request.MinCoordinate}, {request.MaxCoordinate}]", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Depot: {TspAlgorithm.Depot}", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Heuristics: Nearest Neighbor, Greedy Insertion", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Optimization: 2-Opt", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Partitioning Goal: Minimize Makespan (Brute-Force)", "info");

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
                       await SendProgressUpdate(hubContext, "SETUP", $"Warning: Fewer deliveries ({request.NumberOfDeliveries}) than drivers ({request.NumberOfDrivers}). Some drivers may have no route.", "warning");
                    }

                    await SendProgressUpdate(hubContext, "GENERATE", "[1. GENERATING DELIVERIES...]", "step-header");
                    Stopwatch genWatch = Stopwatch.StartNew();
                    List<Delivery> allDeliveries = TspAlgorithm.GenerateRandomDeliveries(
                        request.NumberOfDeliveries, request.MinCoordinate, request.MaxCoordinate);
                    genWatch.Stop();
                    await SendProgressUpdate(hubContext, "GENERATE", $"✔ {allDeliveries.Count} random deliveries generated successfully.", "success", new { Time = FormatTimeSpan(genWatch.Elapsed) });
                    await SendProgressUpdate(hubContext, "GENERATE", $"Time elapsed: {FormatTimeSpan(genWatch.Elapsed)}", "detail");


                    // --- Run Paths and get results back ---
                    nnResultData = await RunOptimizationPath("NN", TspAlgorithm.ConstructNearestNeighborRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    giResultData = await RunOptimizationPath("GI", TspAlgorithm.ConstructGreedyInsertionRoute, allDeliveries, request.NumberOfDrivers, hubContext);

                    await SendProgressUpdate(hubContext, "COMPARE", "[3. COMPARISON & FINAL RESULTS]", "step-header");

                    bool nnBetter = nnResultData.MinMakespan < giResultData.MinMakespan;
                    bool tie = Math.Abs(nnResultData.MinMakespan - giResultData.MinMakespan) < 1e-9;

                    OptimizationResult bestPathResult;
                    string winnerMethod;

                    if (tie || nnBetter)
                    {
                        bestPathResult = nnResultData; // Use the NN result object as the base
                        winnerMethod = tie ? "NN / GI (Tie)" : "Nearest Neighbor";
                    }
                    else
                    {
                        bestPathResult = giResultData; // Use the GI result object as the base
                        winnerMethod = "Greedy Insertion";
                    }

                    await SendProgressUpdate(hubContext, "COMPARE", $"Comparison of Final Makespan:", "info");
                    await SendProgressUpdate(hubContext, "COMPARE", $"- Nearest Neighbor Path : {nnResultData.MinMakespan:F2} {DISTANCE_UNIT}", "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(nnResultData.PathExecutionTimeMs)) });
                    await SendProgressUpdate(hubContext, "COMPARE", $"- Greedy Insertion Path : {giResultData.MinMakespan:F2} {DISTANCE_UNIT}", "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(giResultData.PathExecutionTimeMs)) });
                    await SendProgressUpdate(hubContext, "COMPARE", $"✔ Best Result: '{winnerMethod}' -> Makespan: {bestPathResult.MinMakespan:F2} {DISTANCE_UNIT}", "success");


                    // --- Combine results into the final object ---
                    finalCombinedResult = bestPathResult; // Start with the winner's results
                    finalCombinedResult.BestMethod = winnerMethod;
                    finalCombinedResult.GeneratedDeliveries = new List<Delivery> { TspAlgorithm.Depot }.Concat(allDeliveries).ToList();
                    // Add stats from both paths
                    finalCombinedResult.InitialDistanceNN = nnResultData.InitialDistanceNN;
                    finalCombinedResult.OptimizedDistanceNN = nnResultData.OptimizedDistanceNN;
                    finalCombinedResult.CombinationsCheckedNN = nnResultData.CombinationsCheckedNN;
                    finalCombinedResult.InitialDistanceGI = giResultData.InitialDistanceGI;
                    finalCombinedResult.OptimizedDistanceGI = giResultData.OptimizedDistanceGI;
                    finalCombinedResult.CombinationsCheckedGI = giResultData.CombinationsCheckedGI;


                    await SendProgressUpdate(hubContext, "DETAILS", $"[4. DETAILS OF BEST SOLUTION ({winnerMethod})]", "step-header");
                    await SendProgressUpdate(hubContext, "DETAILS", $"Optimized Route Order (Delivery IDs):", "info");

                    const int maxRouteDisplay = 40;
                    IEnumerable<int> ids = finalCombinedResult.OptimizedRoute?.Select(d => d.Id) ?? Enumerable.Empty<int>();
                    string routeChain;
                     if (finalCombinedResult.OptimizedRoute != null && finalCombinedResult.OptimizedRoute.Count > maxRouteDisplay + 2)
                    {
                        routeChain = string.Join(" -> ", ids.Take(maxRouteDisplay / 2))
                                        + " ... "
                                        + string.Join(" -> ", ids.Skip(finalCombinedResult.OptimizedRoute.Count - maxRouteDisplay / 2));
                    }
                    else
                    {
                        routeChain = string.Join(" -> ", ids);
                    }
                     await SendProgressUpdate(hubContext, "DETAILS", routeChain, "detail");


                    if (finalCombinedResult.OptimizedRoute != null && finalCombinedResult.OptimizedRoute.Any())
                    {
                        finalCombinedResult.DriverRoutes = PopulateDriverRoutes(
                            finalCombinedResult.OptimizedRoute,
                            finalCombinedResult.BestCutIndices,
                            request.NumberOfDrivers
                        );

                        await SendProgressUpdate(hubContext, "DRIVERS", $"[DRIVER SUB-ROUTE DETAILS]", "step-header");
                        foreach(var dr in finalCombinedResult.DriverRoutes)
                        {
                            await SendProgressUpdate(hubContext, $"DRIVERS", $"Driver #{dr.DriverId}:", "info");
                            if (dr.OriginalIndices.Count > 2)
                            {
                                int startIdx = dr.OriginalIndices[1];
                                int endIdx = dr.OriginalIndices[dr.OriginalIndices.Count - 2];
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"- Route Indices : [{startIdx}..{endIdx}]", "detail");
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"- Total Distance: {dr.Distance:F2} {DISTANCE_UNIT}", "detail");
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"  Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
                            } else {
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"- Route Indices : N/A", "detail");
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"- Total Distance: {dr.Distance:F2} {DISTANCE_UNIT}", "detail");
                                await SendProgressUpdate(hubContext, $"DRIVERS", $"  Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
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

                    await SendProgressUpdate(hubContext, "SUMMARY", $"[FINAL SUMMARY]", "header");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Best Makespan ({winnerMethod}): {finalCombinedResult.MinMakespan:F2} {DISTANCE_UNIT}", "result");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Total Execution Time: {FormatTimeSpan(overallStopwatch.Elapsed)}", "info");
                    await SendProgressUpdate(hubContext, "END", $"✔ Optimization Complete!", "success-large");


                    return Results.Ok(finalCombinedResult);
                }
                catch (ArgumentException argEx)
                {
                    overallStopwatch.Stop();
                    finalCombinedResult.ErrorMessage = argEx.Message;
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", $"Input Error: {argEx.Message}", "error");
                    await SendProgressUpdate(hubContext, "END", $"✖ Optimization Failed!", "error-large");
                    return Results.BadRequest(finalCombinedResult);
                }
                catch (Exception ex)
                {
                    overallStopwatch.Stop();
                    Console.WriteLine($"Error during optimization: {ex.Message}{Environment.NewLine}Stack Trace: {ex.StackTrace}");
                    finalCombinedResult.ErrorMessage = app.Environment.IsDevelopment() ? ex.ToString() : "An unexpected error occurred during optimization.";
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", $"Internal Server Error: {finalCombinedResult.ErrorMessage}", "error");
                    await SendProgressUpdate(hubContext, "END", $"✖ Optimization Failed!", "error-large");
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

        static async Task SendProgressUpdate(IHubContext<OptimizationHub> hubContext, string step, string message, string style, object? data = null, bool clearPrevious = false)
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


       // --- Method Signature Changed: Removed 'ref' and returns Task<OptimizationResult> ---
       static async Task<OptimizationResult> RunOptimizationPath(
            string tag,
            Func<List<Delivery>, List<Delivery>> routeBuilder,
            List<Delivery> allDeliveries,
            int numberOfDrivers,
            IHubContext<OptimizationHub> hubContext)
        {
            // --- Create a new result object for this path ---
            var pathResult = new OptimizationResult();
            Stopwatch totalPathWatch = Stopwatch.StartNew();

            string fullMethodName = tag == "NN" ? "Nearest Neighbor" : "Greedy Insertion";
            await SendProgressUpdate(hubContext, tag, $"===== PATH {tag}: {fullMethodName} =====", "header");

            Stopwatch buildSw = Stopwatch.StartNew();
            await SendProgressUpdate(hubContext, $"{tag}.1", $"[{tag}.1 Constructing Initial Route...]", "step");
            List<Delivery> initialRoute = routeBuilder(allDeliveries);
            buildSw.Stop();

            // --- Populate the new pathResult object ---
            double initialDist = TspAlgorithm.ComputeTotalRouteDistance(initialRoute);
            if (tag == "NN") pathResult.InitialDistanceNN = initialDist;
            else pathResult.InitialDistanceGI = initialDist;

            await SendProgressUpdate(hubContext, $"{tag}.1", $"✔ {tag} initial TSP route constructed.", "success", new { Time = FormatTimeSpan(buildSw.Elapsed) });
            await SendProgressUpdate(hubContext, $"{tag}.1", $"* {tag} Initial Route Distance: {initialDist:F2} {DISTANCE_UNIT}", "detail");

            Stopwatch optSw = Stopwatch.StartNew();
            await SendProgressUpdate(hubContext, $"{tag}.2", $"[{tag}.2 Optimizing Route (2-Opt)...]", "step");
            List<Delivery> optimizedRoute = TspAlgorithm.OptimizeRouteUsing2Opt(initialRoute);
            optSw.Stop();

            double optimizedDist = TspAlgorithm.ComputeTotalRouteDistance(optimizedRoute);
             if (tag == "NN") pathResult.OptimizedDistanceNN = optimizedDist;
             else pathResult.OptimizedDistanceGI = optimizedDist;

            double improvement = initialDist - optimizedDist;
            double percent = (initialDist > 1e-9) ? (improvement / initialDist * 100.0) : 0;

            await SendProgressUpdate(hubContext, $"{tag}.2", $"✔ {tag} route optimized.", "success", new { Time = FormatTimeSpan(optSw.Elapsed) });
            await SendProgressUpdate(hubContext, $"{tag}.2", $"* {tag} Optimized Route Distance: {optimizedDist:F2} {DISTANCE_UNIT}", "detail");
            await SendProgressUpdate(hubContext, $"{tag}.2", $"* Improvement Achieved: {improvement:F2} {DISTANCE_UNIT} ({percent:F2}%)", "detail");
            pathResult.OptimizedRoute = optimizedRoute;


            Stopwatch partSw = Stopwatch.StartNew();
            await SendProgressUpdate(hubContext, $"{tag}.3", $"[{tag}.3 Partitioning for {numberOfDrivers} Drivers...]", "step");

            long lastReportedCount = 0;
            Stopwatch progressReportSw = Stopwatch.StartNew();

            TspAlgorithm.ProgressReporter progressCallback = (count) =>
            {
                if (!progressReportSw.IsRunning || progressReportSw.ElapsedMilliseconds > 200)
                {
                    _ = SendProgressUpdate(hubContext, $"{tag}.3", $"Checked {count:N0} combinations...", "progress", null, true);
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
             await SendProgressUpdate(hubContext, $"{tag}.3", $"Checked {combosChecked:N0} combinations.", "progress", null, true);

            pathResult.BestCutIndices = bestCuts;
            pathResult.MinMakespan = minMakespan;
             if (tag == "NN") pathResult.CombinationsCheckedNN = combosChecked;
             else pathResult.CombinationsCheckedGI = combosChecked;


            await SendProgressUpdate(hubContext, $"{tag}.3", $"✔ Optimal partitioning for {tag} route found.", "success", new { Time = FormatTimeSpan(partSw.Elapsed)});
            await SendProgressUpdate(hubContext, $"{tag}.3", $"* Combinations Checked: {combosChecked:N0}", "detail");
            await SendProgressUpdate(hubContext, $"{tag}.3", $"* Optimal Cut Indices: [{string.Join(", ", bestCuts ?? Array.Empty<int>())}]", "detail");
            await SendProgressUpdate(hubContext, $"{tag}.3", $"* Minimum Makespan Achieved ({tag}): {minMakespan:F2} {DISTANCE_UNIT}", "result");

             totalPathWatch.Stop();
             pathResult.PathExecutionTimeMs = totalPathWatch.Elapsed.TotalMilliseconds; // Store path time in the result
             await SendProgressUpdate(hubContext, tag, $"Total time for {tag} path: {FormatTimeSpan(totalPathWatch.Elapsed)}", "info");

             // --- Return the populated result object ---
             return pathResult;
        }

        // --- PopulateDriverRoutes (unchanged from previous version) ---
         static List<DriverRoute> PopulateDriverRoutes(
            List<Delivery> optimizedRoute, int[]? cutIndices, int driverCount)
        {
            var driverRoutes = new List<DriverRoute>();
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
                 if(deliveryCountInRoute <= 0) effectiveCuts = Array.Empty<int>();
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
                var driverRoute = new DriverRoute { DriverId = d };
                int routeStartIndex = currentDeliveryRouteIndex + 1;
                int routeEndIndex = (d <= effectiveCuts.Length) ? effectiveCuts[d - 1] : deliveryCountInRoute;

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

        // --- FormatTimeSpan (unchanged) ---
        public static string FormatTimeSpan(TimeSpan span)
        {
             double timeInMs = span.TotalMilliseconds;
             if (timeInMs >= 1000) return (timeInMs / 1000.0).ToString("F3") + " sec";
             if (timeInMs < 1.0 && timeInMs > 0) return (timeInMs * 1000.0).ToString("F0") + " µs";
             if (timeInMs == 0) return "0 ms";
             return timeInMs.ToString("F2") + " ms";
        }
    }

    // --- TspAlgorithm class (Keep as it is from the previous corrected version, no changes needed here) ---
    public static class TspAlgorithm
    {
        private static readonly Random randomGenerator = new Random();
        public static readonly Delivery Depot = new Delivery(0, 0, 0);
        public delegate void ProgressReporter(long itemsProcessed);

        public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
        {
            if (count <= 0) return new List<Delivery>();
            List<Delivery> deliveries = new List<Delivery>(count);
            HashSet<(int, int)> usedCoords = new HashSet<(int, int)> { (Depot.X, Depot.Y) };
            long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;

             if (count > availableSlots)
            {
                Console.WriteLine($"Warning: Requested {count} deliveries, but only {availableSlots} unique coordinates available. Generating {availableSlots}.");
                 count = (int)Math.Max(0, availableSlots);
             }

            for (int i = 1; i <= count; i++)
            {
                int randomX, randomY;
                int attempts = 0;
                const int maxAttempts = 30000;
                do
                {
                    randomX = randomGenerator.Next(minCoord, maxCoord + 1);
                    randomY = randomGenerator.Next(minCoord, maxCoord + 1);
                    attempts++;
                    if (attempts > maxAttempts) {
                         Console.WriteLine($"Error: Could not find unique coordinates after {maxAttempts} attempts. Range might be too small or exhausted. Returning {deliveries.Count} deliveries.");
                         return deliveries;
                     }
                }
                while (!usedCoords.Add((randomX, randomY)));
                deliveries.Add(new Delivery(i, randomX, randomY));
            }
            return deliveries;
        }

        public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
        {
            List<Delivery> pending = new List<Delivery>(allDeliveries ?? Enumerable.Empty<Delivery>());
            List<Delivery> route = new List<Delivery>((allDeliveries?.Count ?? 0) + 2) { Depot };
            Delivery current = Depot;

            while (pending.Count > 0)
            {
                double bestDistSq = double.MaxValue;
                Delivery? nextDelivery = null;
                int nextIndex = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    Delivery candidate = pending[i];
                    double distSq = CalculateEuclideanDistanceSquared(current, candidate);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        nextDelivery = candidate;
                        nextIndex = i;
                    }
                }

                 if (nextDelivery != null && nextIndex >= 0)
                {
                    route.Add(nextDelivery);
                    current = nextDelivery;
                    pending.RemoveAt(nextIndex);
                }
                else if (pending.Count > 0)
                {
                    Console.WriteLine("Warning: NN could not find next point despite pending deliveries.");
                     break;
                 }
            }
            route.Add(Depot);
            return route;
        }

        public static List<Delivery> ConstructGreedyInsertionRoute(List<Delivery> allDeliveries)
        {
            if (allDeliveries == null || allDeliveries.Count == 0)
            {
                return new List<Delivery> { Depot, Depot };
            }

            List<Delivery> pending = new List<Delivery>(allDeliveries);
            List<Delivery> route = new List<Delivery>(allDeliveries.Count + 2);
            Delivery firstDelivery = pending[0];
            route.Add(Depot);
            route.Add(firstDelivery);
            route.Add(Depot);
            pending.RemoveAt(0);

            while (pending.Count > 0)
            {
                Delivery? bestCandidateToInsert = null;
                int bestInsertionIndex = -1;
                double minIncrease = double.MaxValue;

                foreach (Delivery candidate in pending)
                {
                    for (int i = 0; i < route.Count - 1; i++)
                    {
                        Delivery currentRouteNode = route[i];
                        Delivery nextRouteNode = route[i + 1];
                        double originalEdgeCost = CalculateEuclideanDistance(currentRouteNode, nextRouteNode);
                        double costWithInsertion = CalculateEuclideanDistance(currentRouteNode, candidate) + CalculateEuclideanDistance(candidate, nextRouteNode);
                        double costIncrease = costWithInsertion - originalEdgeCost;

                        if (costIncrease < minIncrease)
                        {
                            minIncrease = costIncrease;
                            bestCandidateToInsert = candidate;
                            bestInsertionIndex = i + 1;
                        }
                    }
                }

                if (bestCandidateToInsert != null && bestInsertionIndex != -1)
                {
                    route.Insert(bestInsertionIndex, bestCandidateToInsert);
                    bool removed = pending.Remove(bestCandidateToInsert);
                    if (!removed) {
                         Console.WriteLine("Warning: Failed to remove inserted candidate from pending list in GI.");
                         pending.RemoveAll(d => d.Id == bestCandidateToInsert.Id);
                     }
                }
                else if (pending.Count > 0)
                {
                    Console.WriteLine("Warning: GI could not find insertion point despite pending deliveries.");
                     break;
                 }
            }
            return route;
        }

        public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
        {
            if (initialRoute == null || initialRoute.Count < 4) return initialRoute ?? new List<Delivery>();

            List<Delivery> currentRoute = new List<Delivery>(initialRoute);
            bool improvementFound = true;
            double improvementThreshold = 1e-9;

            while (improvementFound)
            {
                improvementFound = false;
                 for (int i = 0; i < currentRoute.Count - 3; i++)
                {
                     for (int j = i + 2; j < currentRoute.Count - 1; j++)
                    {
                        Delivery pointA = currentRoute[i];
                        Delivery pointB = currentRoute[i + 1];
                        Delivery pointC = currentRoute[j];
                        Delivery pointD = currentRoute[j + 1];

                        double currentCost = CalculateEuclideanDistance(pointA, pointB) + CalculateEuclideanDistance(pointC, pointD);
                        double swappedCost = CalculateEuclideanDistance(pointA, pointC) + CalculateEuclideanDistance(pointB, pointD);

                         if (swappedCost < currentCost - improvementThreshold)
                        {
                            ReverseSegment(currentRoute, i + 1, j);
                            improvementFound = true;
                         }
                    }
                }
            }
            return currentRoute;
        }

        private static void ReverseSegment(List<Delivery> route, int startIndex, int endIndex)
        {
            while (startIndex < endIndex)
            {
                (route[startIndex], route[endIndex]) = (route[endIndex], route[startIndex]);
                startIndex++;
                endIndex--;
            }
        }

        public static void FindBestPartition(
            List<Delivery> optimizedRoute, int numberOfDrivers, ProgressReporter? reporter,
            out int[]? bestCuts, out double minMakespan, out long combosChecked)
        {
            minMakespan = double.MaxValue;
            combosChecked = 0;
            bestCuts = null;

            if (optimizedRoute == null || optimizedRoute.Count < 2) { minMakespan = 0; return; }

            int cutsNeeded = numberOfDrivers - 1;
            int deliveryCount = optimizedRoute.Count - 2;

            if (deliveryCount <= 0) { bestCuts = Array.Empty<int>(); minMakespan = 0; combosChecked = 1; reporter?.Invoke(combosChecked); return; }
            if (numberOfDrivers <= 1) { bestCuts = Array.Empty<int>(); minMakespan = ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount); combosChecked = 1; reporter?.Invoke(combosChecked); return; }

            bestCuts = new int[cutsNeeded];

            if (deliveryCount < numberOfDrivers)
            {
                for (int i = 0; i < cutsNeeded; i++) { bestCuts[i] = Math.Min(i + 1, deliveryCount); }
                minMakespan = CalculateMakespanForCuts(optimizedRoute, numberOfDrivers, bestCuts);
                combosChecked = 1; reporter?.Invoke(combosChecked); return;
            }

            int[] currentCutCombination = new int[cutsNeeded];
            // Pass the 'bestCuts' array which is allocated above. The ref is needed for the recursive call to update the *same* array instance.
            GenerateCutCombinationsRecursive(optimizedRoute, numberOfDrivers, 1, deliveryCount, 0, currentCutCombination, ref minMakespan, ref bestCuts, ref combosChecked, reporter);
            reporter?.Invoke(combosChecked);
        }

        // Recursive function CAN still use 'ref' if it's NOT async itself.
        private static void GenerateCutCombinationsRecursive(
            List<Delivery> route, int drivers, int searchStartIndex, int maxDeliveryIndex, int currentCutDepth,
            int[] currentCuts, ref double bestMakespanSoFar, ref int[] bestCutCombination, ref long combinationsCounter, ProgressReporter? reporter)
        {
            int cutsNeeded = drivers - 1;
            if (currentCutDepth == cutsNeeded)
            {
                combinationsCounter++;
                double currentMakespan = CalculateMakespanForCuts(route, drivers, currentCuts);
                if (currentMakespan < bestMakespanSoFar)
                {
                    bestMakespanSoFar = currentMakespan;
                    // Ensure bestCutCombination is not null before copying. It's allocated in FindBestPartition before calling this.
                    if(bestCutCombination != null) {
                       Array.Copy(currentCuts, bestCutCombination, cutsNeeded);
                    } else {
                       Console.WriteLine("Error: bestCutCombination was null inside GenerateCutCombinationsRecursive."); // Should not happen
                    }
                }
                if (combinationsCounter % 1000 == 0) reporter?.Invoke(combinationsCounter);

                return;
            }

            int previousCutIndex = (currentCutDepth == 0) ? 0 : currentCuts[currentCutDepth - 1];
            int maxPossibleCutIndex = maxDeliveryIndex - (cutsNeeded - (currentCutDepth + 1));

            for (int i = Math.Max(searchStartIndex, previousCutIndex + 1); i <= maxPossibleCutIndex; i++)
            {
                currentCuts[currentCutDepth] = i;
                GenerateCutCombinationsRecursive(route, drivers, i + 1, maxDeliveryIndex, currentCutDepth + 1, currentCuts, ref bestMakespanSoFar, ref bestCutCombination, ref combinationsCounter, reporter);
            }
        }

        private static double CalculateMakespanForCuts(List<Delivery> route, int drivers, int[] cuts)
        {
            double maxDistance = 0;
            int deliveryCount = route.Count - 2;
            int cutCount = cuts.Length;

            int startDeliveryIndexInRoute = 0;
            for (int driverIndex = 0; driverIndex < drivers; driverIndex++)
            {
                int endDeliveryIndexInRoute = (driverIndex < cutCount) ? cuts[driverIndex] : deliveryCount;

                double currentSegmentDistance = ComputeSubRouteDistanceWithDepot(
                    route, startDeliveryIndexInRoute + 1, endDeliveryIndexInRoute);

                maxDistance = Math.Max(maxDistance, currentSegmentDistance);
                startDeliveryIndexInRoute = endDeliveryIndexInRoute;
            }
            return maxDistance;
        }

        public static double ComputeSubRouteDistanceWithDepot(List<Delivery> route, int startIndexInRoute, int endIndexInRoute)
        {
            if (startIndexInRoute > endIndexInRoute || startIndexInRoute < 1 || endIndexInRoute >= route.Count - 1)
            {
                 return 0;
             }

             if (startIndexInRoute >= route.Count || endIndexInRoute >= route.Count) {
                 Console.WriteLine($"Warning: Index out of bounds in ComputeSubRouteDistanceWithDepot. Start: {startIndexInRoute}, End: {endIndexInRoute}, Route Count: {route.Count}");
                 return 0;
             }

            double totalDistance = CalculateEuclideanDistance(Depot, route[startIndexInRoute]);
            for (int i = startIndexInRoute; i < endIndexInRoute; i++)
            {
                 if (i + 1 < route.Count) {
                     totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
                 } else {
                     Console.WriteLine($"Warning: Index i+1 out of bounds in ComputeSubRouteDistanceWithDepot loop. i: {i}, Route Count: {route.Count}");
                 }
            }
            totalDistance += CalculateEuclideanDistance(route[endIndexInRoute], Depot);
            return totalDistance;
        }

        public static double ComputeTotalRouteDistance(List<Delivery> route)
        {
            if (route == null || route.Count < 2) return 0;
            double totalDistance = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                 if (i + 1 < route.Count) {
                     totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
                 }
            }
            return totalDistance;
        }

        public static double CalculateEuclideanDistance(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0;
            if (d1.X == d2.X && d1.Y == d2.Y) return 0;
            double dx = (double)d2.X - d1.X;
            double dy = (double)d2.Y - d1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double CalculateEuclideanDistanceSquared(Delivery d1, Delivery d2)
        {
             if (d1 == null || d2 == null) return 0;
             if (d1.X == d2.X && d1.Y == d2.Y) return 0;
            double dx = (double)d2.X - d1.X;
            double dy = (double)d2.Y - d1.Y;
            return dx * dx + dy * dy;
        }
    }
}