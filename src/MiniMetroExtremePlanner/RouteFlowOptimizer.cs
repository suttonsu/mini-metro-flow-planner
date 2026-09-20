using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniMetroExtremePlanner
{
    // Combines the useful ideas from the two historical solver prototypes:
    // Mini Metro's octilinear track geometry and route-neighbour search, plus a
    // minimum-cost passenger-flow relaxation over the live line network.
    internal sealed class PassengerFlowAnalysis
    {
        public float AverageCost;
        public float ServedDemand;
        public float UnreachableDemand;
        public float CongestionPenalty;

        public float UnreachableRatio
        {
            get
            {
                float total = ServedDemand + UnreachableDemand;
                return total > 0f ? UnreachableDemand / total : 0f;
            }
        }

        public float Objective
        {
            get
            {
                return AverageCost + UnreachableRatio * 1200f + CongestionPenalty * 35f;
            }
        }

        public string Summary
        {
            get
            {
                return "cost=" + AverageCost.ToString("0.0")
                    + ",unreach=" + UnreachableRatio.ToString("0.00")
                    + ",cong=" + CongestionPenalty.ToString("0.00");
            }
        }
    }

    internal static class RouteFlowOptimizer
    {
        private const float Infinity = 100000000f;
        private const int MaximumUnconnectedCandidates = 5;
        private const int MaximumHubCandidates = 4;
        private const int MaximumFlowEvaluations = 16;

        private sealed class RouteCandidate
        {
            public List<Station> Route;
            public float QuickCost;
            public float FinalCost;
            public float FlowGain;
        }

        // Mini Metro draws a segment as one diagonal leg plus one orthogonal leg.
        // This octile metric represents actual track length more faithfully than
        // straight-line Euclidean distance.
        public static float OctilinearDistance(Station first, Station second)
        {
            if (first == null || second == null) return Infinity;
            float dx = Mathf.Abs(first.Position.x - second.Position.x);
            float dy = Mathf.Abs(first.Position.y - second.Position.y);
            float diagonal = Mathf.Min(dx, dy);
            float straight = Mathf.Max(dx, dy) - diagonal;
            return straight + diagonal * 1.41421356f;
        }

        public static float RouteLength(IList<Station> route)
        {
            if (route == null || route.Count < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                total += OctilinearDistance(route[i - 1], route[i]);
            }
            return total;
        }

        public static PassengerFlowAnalysis Analyze(
            City city,
            List<Station> stations,
            Line candidateLine,
            IList<Station> candidateRoute)
        {
            PassengerFlowAnalysis result = new PassengerFlowAnalysis();
            if (city == null || stations == null || stations.Count == 0) return result;

            int count = stations.Count;
            float[,] costs = new float[count, count];
            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < count; j++) costs[i, j] = i == j ? 0f : Infinity;
            }

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float cost = BestSharedLineCost(city, stations[i], stations[j]);
                    if (cost >= Infinity) continue;
                    costs[i, j] = cost;
                    costs[j, i] = cost;
                }
            }

            if (candidateRoute != null)
            {
                for (int i = 1; i < candidateRoute.Count; i++)
                {
                    int from = stations.IndexOf(candidateRoute[i - 1]);
                    int to = stations.IndexOf(candidateRoute[i]);
                    if (from < 0 || to < 0) continue;
                    float cost = CandidateEdgeCost(
                        candidateRoute[i - 1], candidateRoute[i], candidateLine, candidateRoute.Count);
                    if (cost < costs[from, to])
                    {
                        costs[from, to] = cost;
                        costs[to, from] = cost;
                    }
                }
            }

            // Floyd-Warshall is small and deterministic for Mini Metro maps.  It is
            // the shortest-augmenting-path relaxation of the min-cost-flow proposal:
            // each queued demand unit is routed to its cheapest reachable symbol.
            for (int k = 0; k < count; k++)
            {
                for (int i = 0; i < count; i++)
                {
                    if (costs[i, k] >= Infinity) continue;
                    for (int j = 0; j < count; j++)
                    {
                        float through = costs[i, k] + costs[k, j];
                        if (through < costs[i, j]) costs[i, j] = through;
                    }
                }
            }

            List<StationType> destinationTypes = new List<StationType>();
            for (int i = 0; i < count; i++)
            {
                if (!destinationTypes.Contains(stations[i].Type))
                {
                    destinationTypes.Add(stations[i].Type);
                }
            }

            float weightedCost = 0f;
            for (int origin = 0; origin < count; origin++)
            {
                Station station = stations[origin];
                float demand = DemandWeight(station);
                for (int typeIndex = 0; typeIndex < destinationTypes.Count; typeIndex++)
                {
                    StationType destination = destinationTypes[typeIndex];
                    if (destination == station.Type) continue;
                    float best = Infinity;
                    for (int target = 0; target < count; target++)
                    {
                        if (stations[target].Type == destination && costs[origin, target] < best)
                        {
                            best = costs[origin, target];
                        }
                    }
                    if (best >= Infinity)
                    {
                        result.UnreachableDemand += demand;
                    }
                    else
                    {
                        result.ServedDemand += demand;
                        weightedCost += best * demand;
                    }
                }

                int capacity = Mathf.Max(1, station.PeepCapacity);
                float queueRatio = (float)station.PeepCount / capacity;
                result.CongestionPenalty += Mathf.Max(0f, queueRatio - 0.55f);
                if (station.NumPeepsOverCapacity > 0)
                {
                    result.CongestionPenalty += 0.75f + station.ExpiryTimerCompletion;
                }
            }
            result.AverageCost = result.ServedDemand > 0f
                ? weightedCost / result.ServedDemand
                : 0f;
            result.CongestionPenalty /= Mathf.Max(1, count);
            return result;
        }

        public static float FlowGain(
            City city,
            List<Station> stations,
            PassengerFlowAnalysis baseline,
            Line candidateLine,
            IList<Station> candidateRoute)
        {
            if (baseline == null) baseline = Analyze(city, stations, null, null);
            PassengerFlowAnalysis after = Analyze(city, stations, candidateLine, candidateRoute);
            return baseline.Objective - after.Objective;
        }

        public static List<Station> FindBestNewLineRoute(
            City city,
            List<Station> stations,
            List<Station> unconnected,
            Func<Station, Station, bool> isBlocked,
            out float flowGain)
        {
            flowGain = 0f;
            if (city == null || stations == null || unconnected == null || unconnected.Count == 0)
            {
                return null;
            }

            List<Station> unconnectedPool = new List<Station>(unconnected);
            unconnectedPool.Sort(delegate(Station first, Station second)
            {
                return ComparePriority(second, first);
            });
            if (unconnectedPool.Count > MaximumUnconnectedCandidates)
            {
                unconnectedPool.RemoveRange(
                    MaximumUnconnectedCandidates,
                    unconnectedPool.Count - MaximumUnconnectedCandidates);
            }

            List<Station> hubs = new List<Station>();
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i] != null && stations[i].LineCount > 0) hubs.Add(stations[i]);
            }
            hubs.Sort(delegate(Station first, Station second)
            {
                return CompareHubPriority(second, first);
            });
            if (hubs.Count > MaximumHubCandidates)
            {
                hubs.RemoveRange(MaximumHubCandidates, hubs.Count - MaximumHubCandidates);
            }

            List<Station> pool = new List<Station>();
            for (int i = 0; i < unconnectedPool.Count; i++) AddUnique(pool, unconnectedPool[i]);
            for (int i = 0; i < hubs.Count; i++) AddUnique(pool, hubs[i]);

            List<RouteCandidate> candidates = new List<RouteCandidate>();
            for (int first = 0; first < pool.Count; first++)
            {
                for (int second = 0; second < pool.Count; second++)
                {
                    if (first == second) continue;
                    List<Station> pair = new List<Station>();
                    pair.Add(pool[first]);
                    pair.Add(pool[second]);
                    AddCandidate(candidates, city, pair, isBlocked);

                    for (int third = 0; third < pool.Count; third++)
                    {
                        if (third == first || third == second) continue;
                        List<Station> triple = new List<Station>(pair);
                        triple.Add(pool[third]);
                        AddCandidate(candidates, city, triple, isBlocked);
                    }
                }
            }
            if (candidates.Count == 0) return null;

            candidates.Sort(delegate(RouteCandidate first, RouteCandidate second)
            {
                return first.QuickCost.CompareTo(second.QuickCost);
            });
            PassengerFlowAnalysis baseline = Analyze(city, stations, null, null);
            RouteCandidate best = null;
            int evaluations = Mathf.Min(MaximumFlowEvaluations, candidates.Count);
            for (int i = 0; i < evaluations; i++)
            {
                RouteCandidate candidate = candidates[i];
                candidate.FlowGain = FlowGain(city, stations, baseline, null, candidate.Route);
                candidate.FinalCost = candidate.QuickCost - candidate.FlowGain * 0.85f;
                if (best == null || candidate.FinalCost < best.FinalCost) best = candidate;
            }
            if (best == null) return null;
            flowGain = best.FlowGain;
            return best.Route;
        }

        private static void AddCandidate(
            List<RouteCandidate> candidates,
            City city,
            List<Station> route,
            Func<Station, Station, bool> isBlocked)
        {
            int unconnected = 0;
            int connected = 0;
            for (int i = 0; i < route.Count; i++)
            {
                if (route[i] == null) return;
                if (route[i].LineCount <= 0) unconnected++;
                else connected++;
                if (i > 0 && isBlocked != null && isBlocked(route[i - 1], route[i])) return;
            }
            if (unconnected <= 0) return;
            // Preserve multi-line reachability from metro-master: once a network
            // exists, a new route must include a transfer into that network.
            if (city.LineCount > 0 && connected <= 0) return;

            candidates.Add(new RouteCandidate
            {
                Route = route,
                QuickCost = QuickRouteCost(route)
            });
        }

        private static float QuickRouteCost(IList<Station> route)
        {
            float cost = RouteLength(route);
            List<StationType> types = new List<StationType>();
            for (int i = 0; i < route.Count; i++)
            {
                Station station = route[i];
                if (types.Contains(station.Type)) cost += 92f;
                else
                {
                    types.Add(station.Type);
                    cost -= 38f;
                }
                cost -= DemandPriority(station) * 18f;
                if (station.LineCount <= 0) cost -= 115f;
                else cost -= station.LineCount * 42f;
            }
            return cost;
        }

        private static float BestSharedLineCost(City city, Station first, Station second)
        {
            float best = Infinity;
            for (int i = 0; i < city.LineCount; i++)
            {
                Line line = city.GetLine(i);
                if (line == null || !line.ContainsStation(first) || !line.ContainsStation(second))
                {
                    continue;
                }
                float cost = CandidateEdgeCost(first, second, line, line.LiveLinkCount + 1);
                if (cost < best) best = cost;
            }
            return best;
        }

        private static float CandidateEdgeCost(
            Station first,
            Station second,
            Line line,
            int routeStations)
        {
            float length = OctilinearDistance(first, second);
            float service = 1f;
            float routePenalty = Mathf.Max(0, routeStations - 2) * 1.5f;
            if (line != null)
            {
                float capacity = 1f + line.TrainCount + line.CarriageCount * 0.65f;
                service = Mathf.Max(0.75f, Mathf.Sqrt(capacity));
                routePenalty += line.LiveLinkCount * 1.5f;
                routePenalty += Mathf.Max(0f, line.PeepCount - capacity * 6f) * 1.8f;
            }
            return length / service + routePenalty;
        }

        private static float DemandWeight(Station station)
        {
            if (station == null) return 1f;
            return 1f + station.PeepCount
                + station.NumPeepsOverCapacity * 4f
                + station.ExpiryTimerCompletion * station.NumPeepsOverCapacity * 4f;
        }

        private static float DemandPriority(Station station)
        {
            if (station == null) return 0f;
            int capacity = Mathf.Max(1, station.PeepCapacity);
            float pressure = (float)station.PeepCount / capacity;
            if (station.NumPeepsOverCapacity > 0)
            {
                pressure += 0.5f + station.ExpiryTimerCompletion;
            }
            return pressure + station.PeepCount * 0.08f;
        }

        private static int ComparePriority(Station first, Station second)
        {
            int comparison = DemandPriority(first).CompareTo(DemandPriority(second));
            if (comparison != 0) return comparison;
            comparison = first.Position.x.CompareTo(second.Position.x);
            if (comparison != 0) return comparison;
            return first.Position.y.CompareTo(second.Position.y);
        }

        private static int CompareHubPriority(Station first, Station second)
        {
            float firstScore = first.LineCount * 2f + DemandPriority(first);
            float secondScore = second.LineCount * 2f + DemandPriority(second);
            int comparison = firstScore.CompareTo(secondScore);
            if (comparison != 0) return comparison;
            return ComparePriority(first, second);
        }

        private static void AddUnique(List<Station> stations, Station station)
        {
            if (station != null && !stations.Contains(station)) stations.Add(station);
        }
    }
}
