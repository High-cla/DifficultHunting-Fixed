using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Xna.Framework;

using FarseerPhysics;
using Voronoi2;

using HarmonyLib;

using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking; // used by the server


namespace AdvancedAutopilot
{
    public partial class Mod : IAssemblyPlugin
    {
        public static void PatchSharedFindPath()
        {
            harmony.Patch(
                original: typeof(PathFinder).GetMethod("FindPath", BindingFlags.Public
                                                                 | BindingFlags.Instance
                                                                 | BindingFlags.Static
                                                                 | BindingFlags.GetField
                                                                 | BindingFlags.SetField
                                                                 | BindingFlags.GetProperty
                                                                 | BindingFlags.SetProperty),
                prefix: new HarmonyMethod(typeof(Mod).GetMethod("PathFinder_FindPath_Replace"))
            );
        }

        public static void PathFinder_FindPath_Replace(PathFinder __instance, Vector2 start, Vector2 end, ref SteeringPath __result, Submarine hostSub = null,
                                                               string errorMsgStr = null, float minGapSize = 0, Func<PathNode, bool> startNodeFilter = null,
                                                               Func<PathNode, bool> endNodeFilter = null, Func<PathNode, bool> nodeFilter = null,
                                                               bool checkVisibility = true, float outsideNodePenalty = 0)
        {
            PathFinder _ = __instance;

            //Log($"FindPath called. start: {start}, end: {end}, hostSub: {hostSub?.Info?.Name ?? "null"}, minGapSize: {minGapSize}, checkVisibility: {checkVisibility}, outsideNodePenalty: {outsideNodePenalty}", Color.Magenta);

            foreach (PathNode node in _.nodes)
            {
                node.ResetBlocked();
            }

            // First calculate the temp positions for all nodes.
            foreach (PathNode node in _.nodes)
            {
                node.TempPosition = node.Position;
                var wpSub = node.Waypoint.Submarine;
                if (hostSub != null && wpSub == null)
                {
                    // inside and targeting outside
                    node.TempPosition -= hostSub.SimPosition;
                }
                else if (wpSub != null && hostSub != null && wpSub != hostSub)
                {
                    // different subs
                    node.TempPosition -= hostSub.SimPosition - wpSub.SimPosition;
                }
                else if (hostSub == null && wpSub != null)
                {
                    // Outside and targeting inside 
                    node.TempPosition += wpSub.SimPosition;
                }
            }

            //sort nodes roughly according to distance
            _.sortedNodes.Clear();
            PathNode startNode = null;
            foreach (PathNode node in _.nodes)
            {
                float xDiff = Math.Abs(start.X - node.TempPosition.X);
                float yDiff = Math.Abs(start.Y - node.TempPosition.Y);
                //higher cost for vertical movement when inside the sub
                if (_.InsideSubmarine && !(node.Waypoint.Submarine?.Info?.IsRuin ?? false))
                {
                    if (yDiff > 1.0f && node.Waypoint.Ladders == null && node.Waypoint.Stairs == null)
                    {
                        yDiff += 10.0f;
                    }
                    //only apply the higher cost for vertical movement if it's more than a meter
                    //(small differences can be caused by non-meaningful variance in the vertical position of the waypoint, we only care about actually going up/down e.g. ladders or stairs)
                    if (Math.Abs(yDiff) > 1.0f)
                    {
                        yDiff *= 10.0f;
                    }
                }
                node.TempDistance = xDiff + yDiff;

                //much higher cost to waypoints that are outside
                if (node.Waypoint.CurrentHull == null && _.ApplyPenaltyToOutsideNodes) { node.TempDistance *= 10.0f; }

                //optimization: node extremely far, don't try to use it as a start node
                if (node.TempDistance > (_.InsideSubmarine ? 100.0f : 800.0f))
                {
                    continue;
                }
                //optimization: node close enough. If it's valid, choose it as the start node and skip the more exhaustive search for the closest one
                if (node.TempDistance < FarseerPhysics.ConvertUnits.ToSimUnits(AIObjectiveGetItem.DefaultReach))
                {
                    if (IsValidStartNode(node))
                    {
                        startNode = node;
                        break;
                    }
                }
                //prefer nodes that are closer to the end position
                node.TempDistance += (Math.Abs(end.X - node.TempPosition.X) + Math.Abs(end.Y - node.TempPosition.Y)) / 100.0f;

                int i = 0;
                while (i < _.sortedNodes.Count && _.sortedNodes[i].TempDistance < node.TempDistance)
                {
                    i++;
                }
                _.sortedNodes.Insert(i, node);
            }

            //find the most suitable start node, starting from the ones that are the closest
            if (startNode == null)
            {
                foreach (PathNode node in _.sortedNodes)
                {
                    if (IsValidStartNode(node))
                    {
                        startNode = node;
                        break;
                    }
                }
            }

            if (startNode == null)
            {
#if DEBUG
                //DebugConsole.NewMessage("Pathfinding error, couldn't find a start node. " + errorMsgStr, Color.DarkRed);
                DebugConsole.NewMessage("Pathfinding error, couldn't find a start node. " + errorMsgStr, Color.DarkRed);
#endif
                __result = new SteeringPath(true);
                return;
            }

            //sort nodes again, now based on distance from the end position
            _.sortedNodes.Clear();
            PathNode endNode = null;
            foreach (PathNode node in _.nodes)
            {
                node.TempDistance = Vector2.DistanceSquared(end, node.TempPosition);
                if (_.InsideSubmarine)
                {
                    if (_.ApplyPenaltyToOutsideNodes)
                    {
                        //much higher cost to waypoints that are outside
                        if (node.Waypoint.CurrentHull == null) { node.TempDistance *= 10.0f; }
                    }
                    //avoid stopping at a doorway
                    if (node.Waypoint.ConnectedDoor != null) { node.TempDistance *= 10.0f; }
                }
                //optimization: node extremely far (> 100m / 800 m) from the end position, don't try to use it as an end node
                if (node.TempDistance > (_.InsideSubmarine ? 100.0f * 100.0f : 800.0f * 800.0f))
                {
                    continue;
                }
                //optimization: node extremely close (< 1 m). If it's valid, choose it as the end node and skip the more exhaustive search for the closest one
                if (node.TempDistance < 1.0f)
                {
                    if (IsValidEndNode(node))
                    {
                        endNode = node;
                        break;
                    }
                }
                int i = 0;
                while (i < _.sortedNodes.Count && _.sortedNodes[i].TempDistance < node.TempDistance)
                {
                    i++;
                }
                _.sortedNodes.Insert(i, node);
            }
            if (endNode == null)
            {
                //find the most suitable end node, starting from the ones closest to the end position
                foreach (PathNode node in _.sortedNodes)
                {
                    if (IsValidEndNode(node))
                    {
                        endNode = node;
                        break;
                    }
                }
            }
            if (endNode == null)
            {
#if DEBUG
                //DebugConsole.NewMessage("Pathfinding error, couldn't find an end node. " + errorMsgStr, Color.DarkRed);
                DebugConsole.NewMessage("Pathfinding error, couldn't find an end node. " + errorMsgStr, Color.DarkRed);
#endif
                __result = new SteeringPath(true);
                return;
            }
            float outsideNodeCostPenalty = outsideNodePenalty;
            if (_.ApplyPenaltyToOutsideNodes)
            {
                outsideNodeCostPenalty += 100;
            }
            __result = FindPath(_, startNode, endNode, nodeFilter, errorMsgStr, minGapSize, outsideNodeCostPenalty);
            return;

            bool IsValidStartNode(PathNode node) => IsValidNode(node, (_.isCharacter, start), startNodeFilter);

            bool IsValidEndNode(PathNode node) => IsValidNode(node, (_.isCharacter && checkVisibility, end), endNodeFilter);

            bool IsValidNode(PathNode node, (bool check, Vector2 start) visibilityCheck, Func<PathNode, bool> extraFilter)
            {
                if (nodeFilter != null && !nodeFilter(node)) { return false; }
                if (extraFilter != null && !extraFilter(node)) { return false; }
                if (_.GetSingleNodePenalty != null && _.GetSingleNodePenalty(node) == null) { return false; }
                if (node.Waypoint.ConnectedGap != null)
                {
                    if (!_.CanFitThroughGap(node.Waypoint.ConnectedGap, minGapSize)) { return false; }
                }
                if (visibilityCheck.check)
                {
                    var body = Submarine.PickBody(visibilityCheck.start, node.TempPosition,
                        collisionCategory: Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionStairs);
                    if (body != null)
                    {
                        if (body.UserData is Submarine) { return false; }
                        if (body.UserData is Structure s && !s.IsPlatform) { return false; }
                        if (body.UserData is Voronoi2.VoronoiCell) { return false; }
                        if (body.UserData is Item && body.FixtureList[0].CollisionCategories.HasFlag(Physics.CollisionWall)) { return false; }
                    }
                }
                return true;
            }
        }

        private static SteeringPath FindPath(PathFinder __instance, PathNode start, PathNode end, Func<PathNode, bool> filter = null, string errorMsgStr = "", float minGapSize = 0f, float outsideNodePenalty = 0f)
        {
            PathFinder _ = __instance;
            if (start == end)
            {
                var path1 = new SteeringPath();
                path1.AddNode(start.Waypoint);
                return path1;
            }

            foreach (PathNode node in _.nodes)
            {
                node.Parent = null;
                node.state = 0;
                node.F = 0.0f;
                node.G = 0.0f;
                node.H = 0.0f;
            }

            start.state = 1;
            while (true)
            {
                PathNode currNode = null;
                float dist = float.MaxValue;
                foreach (PathNode node in _.nodes)
                {
                    if (node.state != 1 || node.F > dist) { continue; }
                    if (filter != null && !filter(node)) { continue; }
                    if (node.Waypoint.ConnectedGap != null)
                    {
                        if (!CanFitThroughGap(node.Waypoint.ConnectedGap, minGapSize)) { continue; }
                    }
                    dist = node.F;
                    currNode = node;
                }

                if (currNode == null || currNode == end) { break; }

                currNode.state = 2;

                for (int i = 0; i < currNode.connections.Count; i++)
                {
                    PathNode nextNode = currNode.connections[i];

                    switch (nextNode.state)
                    {
                        //a node that hasn't been searched yet
                        case 0:
                            {
                                nextNode.H = Vector2.Distance(nextNode.Position, end.Position);
                                float cost = CalculateNodeCost();
                                if (cost < float.PositiveInfinity)
                                {
                                    nextNode.G = cost;
                                    nextNode.F = nextNode.G + nextNode.H;
                                    nextNode.Parent = currNode;
                                    nextNode.state = 1;
                                }
                                else
                                {
                                    // Set searched and invalid.
                                    nextNode.state = -1;
                                }
                                break;
                            }
                        //node that has been searched
                        case 1 or -1:
                            {
                                float tempG = CalculateNodeCost();
                                //only use if this new route is better than the 
                                //route the node was a part of
                                if (tempG < nextNode.G)
                                {
                                    nextNode.G = tempG;
                                    nextNode.F = nextNode.G + nextNode.H;
                                    nextNode.Parent = currNode;
                                    nextNode.state = 1;
                                }
                                break;
                            }
                    }

                    float CalculateNodeCost()
                    {
                        float penalty = 0f;
                        if (_.GetNodePenalty != null)
                        {
                            float? nodePenalty = _.GetNodePenalty(currNode, nextNode);
                            if (nodePenalty.HasValue)
                            {
                                penalty += nodePenalty.Value;
                            }
                            else
                            {
                                return float.PositiveInfinity;
                            }
                        }
                        if (currNode.Waypoint.CurrentHull == null)
                        {
                            penalty += outsideNodePenalty;
                        }
                        return currNode.G + currNode.distances[i] + penalty;
                    }
                }
            }

            if (end.state == 0 || end.Parent == null)
            {
#if DEBUG
                if (errorMsgStr != null)
                {
                    DebugConsole.NewMessage("Path not found. " + errorMsgStr, Color.Yellow);
                }
#endif
                return new SteeringPath(true);
            }

            SteeringPath path = new SteeringPath();
            List<WayPoint> finalPath = new List<WayPoint>();

            PathNode pathNode = end;
            while (pathNode != start && pathNode != null)
            {
                finalPath.Add(pathNode.Waypoint);

                //(there was one bug report that seems to have been caused by this loop never terminating:
                //couldn't reproduce or figure out what caused it, but here's a workaround that prevents the game from crashing in case it happens again)

                //should be fixed now, was most likely caused by the parent fields of the nodes not being cleared before starting the pathfinding
                if (finalPath.Count > _.nodes.Count)
                {
#if DEBUG
                    DebugConsole.ThrowError("Pathfinding error: constructing final path failed");
#endif
                    return new SteeringPath(true);
                }

                path.Cost += pathNode.F;
                pathNode = pathNode.Parent;
            }

            finalPath.Add(start.Waypoint);
            for (int i = finalPath.Count - 1; i >= 0; i--)
            {
                path.AddNode(finalPath[i]);
            }
            System.Diagnostics.Debug.Assert(finalPath.Count == path.Nodes.Count);

            return path;
        }

        private static bool CanFitThroughGap(Gap gap, float minWidth) => gap.IsHorizontal ? gap.RectHeight > minWidth : gap.RectWidth > minWidth;
    }
}