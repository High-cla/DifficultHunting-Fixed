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


    public static void PatchSharedUpdateAutoPilot()
    {
      harmony.Patch(
        original: typeof(Steering).GetMethod("UpdateAutoPilot", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_UpdateAutoPilot_Replace"))
      );
    }

    public static void PatchSharedTargetVelocity()
    {
      harmony.Patch(
        original: typeof(Steering).GetProperty("TargetVelocity", AccessTools.all).SetMethod,
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_TargetVelocity_Set_Replace"))
      );
    }

    public static void PatchSharedUpdate()
    {
      harmony.Patch(
        original: typeof(Steering).GetMethod("Update", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_Update_Replace"))
      );
    }

    public static void PatchSharedUpdatePath()
    {
      harmony.Patch(
        original: typeof(Steering).GetMethod("UpdatePath", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_UpdatePath_Replace"))
      );
    }

    public static void PatchSharedGetSteeringVelocity()
    {
      harmony.Patch(
        original: typeof(Steering).GetMethod("GetSteeringVelocity", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_GetSteeringVelocity_Replace"))
      );
    }

    public static bool Steering_TargetVelocity_Set_Replace(Steering __instance, ref Vector2 value)
    {
      Steering _ = __instance;

      if (!MathUtils.IsValid(value))
      {
        if (!MathUtils.IsValid(_.targetVelocity))
        {
          _.targetVelocity = Vector2.Zero;
        }
        return false;
      }

      // Clamp X and Y components
      value.X = MathHelper.Clamp(value.X, -100.0f, 100.0f);
      value.Y = MathHelper.Clamp(value.Y, -100.0f, 100.0f);

      _.targetVelocity = value;

      // Skip original method
      return false;
    }

    public static bool Steering_GetSteeringVelocity_Replace(Steering __instance, Vector2 worldPosition, float slowdownAmount, ref Vector2 __result)
    {
      Steering _ = __instance;

      // 贪心：直接朝目标开，靠近时减速
      Vector2 toTarget = worldPosition - _.controlledSub.WorldPosition;
      float distance = toTarget.Length();

      // 距离远：全速。距离近：提前大幅减速，防止过冲
      float speed = 200.0f;
      if (distance < 3000.0f)
      {
        // 从3000单位开始线性减速，给足够刹车距离
        speed = 200.0f * (distance / 3000.0f);
      }
      if (distance < 500.0f)
      {
        // 最后500：进一步降到低速，精细对接
        speed = Math.Min(speed, 20.0f * (distance / 500.0f));
      }

      // 直接朝目标方向
      if (distance > 0.01f)
        __result = Vector2.Normalize(toTarget) * speed;
      else
        __result = Vector2.Zero;

      // 轨迹平滑：动量过滤，减少急转弯
      smoothedVelocity = Vector2.Lerp(smoothedVelocity, __result, SmoothFactor);
      __result = smoothedVelocity;

      // Skip original method
      return false;
    }

    // https://github.com/evilfactory/LuaCsForBarotrauma/blob/ad837423a8d71666dc0a5621713e2ab1fe7e2802/Barotrauma/BarotraumaShared/SharedSource/Items/Components/Machines/Steering.cs#L431
    public static bool Steering_UpdateAutoPilot_Replace(Steering __instance, float deltaTime)
    {
      Steering _ = __instance;

      if (_.controlledSub == null)
      {
        return false;
      }

      if (_.posToMaintain != null)
      {
        Vector2 steeringVel = _.GetSteeringVelocity((Vector2)_.posToMaintain, 10.0f);
        _.TargetVelocity = Vector2.Lerp(_.TargetVelocity, steeringVel, Steering.AutoPilotSteeringLerp);
        _.showIceSpireWarning = false;
        return false;
      }

      _.autopilotRayCastTimer -= deltaTime;
      _.autopilotRecalculatePathTimer -= deltaTime;
      if (_.autopilotRecalculatePathTimer <= 0.0f)
      {
        // Periodically recalculate the path in case the sub ends up in a position
        // where it can't keep traversing the initially calculated path.
        _.UpdatePath();
        _.autopilotRecalculatePathTimer = RecalculatePathInterval;
      }

      if (_.steeringPath == null)
      {
        _.showIceSpireWarning = false;
        return false;
      }

      _.steeringPath.CheckProgress(ConvertUnits.ToSimUnits(_.controlledSub.WorldPosition), 50.0f);

      _.connectedSubUpdateTimer -= deltaTime;
      if (_.connectedSubUpdateTimer <= 0.0f)
      {
        _.connectedSubs.Clear();
        _.connectedSubs.AddRange(_.controlledSub.GetConnectedSubs());
        _.connectedSubUpdateTimer = Steering.ConnectedSubUpdateInterval;
      }

      if (_.autopilotRayCastTimer <= 0.0f && _.steeringPath.NextNode != null)
      {
        Vector2 diff = ConvertUnits.ToSimUnits(_.steeringPath.NextNode.Position - _.controlledSub.WorldPosition);

        // If the node is close enough, check if it's visible.
        float lengthSqr = diff.LengthSquared();
        if (lengthSqr > 0.001f && lengthSqr < AutopilotMinDistToPathNode * AutopilotMinDistToPathNode)
        {
          diff = Vector2.Normalize(diff);

          // Check if the next waypoint is visible from all corners of the sub
          // (i.e. if we can navigate directly towards it or if there's obstacles in the way).
          bool nextVisible = true;
          for (int x = -1; x < 2; x += 2)
          {
            for (int y = -1; y < 2; y += 2)
            {
              Vector2 cornerPos = new Vector2(_.controlledSub.Borders.Width * x, _.controlledSub.Borders.Height * y) / 2.0f;
              cornerPos = ConvertUnits.ToSimUnits(cornerPos * 1.1f + _.controlledSub.WorldPosition);

              float dist = Vector2.Distance(cornerPos, _.steeringPath.NextNode.SimPosition);

              if (Submarine.PickBody(cornerPos, cornerPos + diff * dist, null, Physics.CollisionLevel) == null)
              {
                continue;
              }

              nextVisible = false;
              x = 2; // break outer loop
              y = 2; // break inner loop
            }
          }

          if (nextVisible)
          {
            _.steeringPath.SkipToNextNode();
          }
        }

        _.autopilotRayCastTimer = AutopilotRayCastInterval;
      }

      // 贪心：前瞻到更远的节点，路径尽量直
      Vector2 newVelocity = Vector2.Zero;
      if (_.steeringPath.CurrentNode != null)
      {
        Vector2 targetPos = _.steeringPath.CurrentNode.WorldPosition;

        // 如果距离当前节点近，看向下一个节点（平滑过渡）
        if (_.steeringPath.NextNode != null)
        {
          float distToCurrent = Vector2.Distance(_.controlledSub.WorldPosition, _.steeringPath.CurrentNode.WorldPosition);
          if (distToCurrent < 2000.0f)
          {
            float blend = distToCurrent / 2000.0f;
            targetPos = Vector2.Lerp(_.steeringPath.CurrentNode.WorldPosition, _.steeringPath.NextNode.WorldPosition, 1.0f - blend);
          }
        }

        newVelocity = _.GetSteeringVelocity(targetPos, 1.0f);
      }

      Vector2 avoidDist = new Vector2(
          Math.Max(1000.0f * Math.Abs(_.controlledSub.Velocity.X), _.controlledSub.Borders.Width * 0.1f),
          Math.Max(1000.0f * Math.Abs(_.controlledSub.Velocity.Y), _.controlledSub.Borders.Height * 0.1f));

      float avoidRadius = avoidDist.Length();
      float damagingWallAvoidRadius = MathHelper.Clamp(avoidRadius * 1.5f, 100.0f, 1000.0f);

      Vector2 newAvoidStrength = Vector2.Zero;
      _.debugDrawObstacles.Clear();

      // Steer away from nearby walls
      _.showIceSpireWarning = false;
      var closeCells = Level.Loaded.GetCells(_.controlledSub.WorldPosition, 4);
      foreach (VoronoiCell cell in closeCells)
      {
        if (cell.DoesDamage || cell.Body is { BodyType: BodyType.Dynamic })
        {
          foreach (GraphEdge edge in cell.Edges)
          {
            Vector2 closestPoint = MathUtils.GetClosestPointOnLineSegment(
                edge.Point1 + cell.Translation,
                edge.Point2 + cell.Translation,
                _.controlledSub.WorldPosition);

            Vector2 diff = closestPoint - _.controlledSub.WorldPosition;
            float dist = diff.Length() - Math.Max(_.controlledSub.Borders.Width, _.controlledSub.Borders.Height) / 2;
            if (dist > damagingWallAvoidRadius)
            {
              continue;
            }

            Vector2 normalizedDiff = Vector2.Normalize(diff);
            float dot = Vector2.Dot(normalizedDiff, _.controlledSub.Velocity);

            float avoidStrength = MathHelper.Clamp(MathHelper.Lerp(1.0f, 0.0f, dist / damagingWallAvoidRadius - dot), 0.0f, 1.0f);
            Vector2 avoid = -normalizedDiff * avoidStrength;
            newAvoidStrength += avoid;
            _.debugDrawObstacles.Add(new Steering.ObstacleDebugInfo(edge, edge.Center, 1.0f, avoid, cell.Translation));

            if (dot > 0.0f && cell.DoesDamage)
            {
              _.showIceSpireWarning = true;
            }
          }

          continue;
        }

        foreach (GraphEdge edge in cell.Edges)
        {
          if (!MathUtils.GetLineSegmentIntersection(
                  edge.Point1 + cell.Translation,
                  edge.Point2 + cell.Translation,
                  _.controlledSub.WorldPosition,
                  cell.Center,
                  out Vector2 intersection))
          {
            continue;
          }

          Vector2 diff = _.controlledSub.WorldPosition - intersection;

          // Far enough -> ignore
          if (Math.Abs(diff.X) > avoidDist.X && Math.Abs(diff.Y) > avoidDist.Y)
          {
            _.debugDrawObstacles.Add(new Steering.ObstacleDebugInfo(edge, intersection, 0.0f, Vector2.Zero, Vector2.Zero));
            continue;
          }

          if (diff.LengthSquared() < 1.0f)
          {
            diff = Vector2.UnitY;
          }

          Vector2 normalizedDiff = Vector2.Normalize(diff);
          float dot = _.controlledSub.Velocity == Vector2.Zero ? 0.0f : Vector2.Dot(_.controlledSub.Velocity, -normalizedDiff);

          // Not heading towards the wall -> ignore
          if (dot < 1.0)
          {
            _.debugDrawObstacles.Add(new Steering.ObstacleDebugInfo(edge, intersection, dot, Vector2.Zero, cell.Translation));
            continue;
          }

          Vector2 change = (normalizedDiff * Math.Max((avoidRadius - diff.Length()), 0.0f)) / avoidRadius;
          if (change.LengthSquared() < 0.001f)
          {
            continue;
          }

          newAvoidStrength += change * (dot - 1.0f);
          _.debugDrawObstacles.Add(new Steering.ObstacleDebugInfo(edge, intersection, dot - 1.0f, change * (dot - 1.0f), cell.Translation));
        }
      }

      _.avoidStrength = Vector2.Lerp(_.avoidStrength, newAvoidStrength, deltaTime * 10.0f);

      // Speed-dependent damping: reduce obstacle avoidance influence and steering responsiveness at high speeds
      float currentSpeed = _.controlledSub.Velocity.Length();
      float avoidDamping = 1.0f + MathHelper.Clamp(currentSpeed / 15.0f, 0.0f, 2.0f);
      Vector2 targetWithAvoidance = newVelocity + _.avoidStrength * (100.0f / avoidDamping);

      // Dynamic steering lerp - react more slowly at high speeds to prevent oscillation
      float dynamicLerp = MathHelper.Lerp(AutoPilotSteeringLerp, AutoPilotSteeringLerp * 0.25f, MathHelper.Clamp(currentSpeed / 20.0f, 0.0f, 1.0f));
      _.TargetVelocity = Vector2.Lerp(_.TargetVelocity, targetWithAvoidance, dynamicLerp);

      // Steer away from other subs
      foreach (Submarine sub in Submarine.Loaded)
      {
        if (sub == _.controlledSub || _.connectedSubs.Contains(sub))
        {
          continue;
        }

        Point sizeSum = _.controlledSub.Borders.Size + sub.Borders.Size;
        Vector2 minDist = sizeSum.ToVector2() / 2;
        Vector2 diff = _.controlledSub.WorldPosition - sub.WorldPosition;
        float xDist = Math.Abs(diff.X);
        float yDist = Math.Abs(diff.Y);
        Vector2 maxAvoidDistance = minDist * 2;

        if (xDist > maxAvoidDistance.X || yDist > maxAvoidDistance.Y)
        {
          // Far enough -> ignore
          continue;
        }

        float dot = _.controlledSub.Velocity == Vector2.Zero ? 0.0f : Vector2.Dot(Vector2.Normalize(_.controlledSub.Velocity), -diff);
        if (dot < 0.0f)
        {
          // Heading away -> ignore
          continue;
        }

        float distanceFactor = MathHelper.Lerp(0, 1, MathUtils.InverseLerp(maxAvoidDistance.X + maxAvoidDistance.Y, minDist.X + minDist.Y, xDist + yDist));
        float velocityFactor = MathHelper.Lerp(0, 1, MathUtils.InverseLerp(0, 3, _.controlledSub.Velocity.Length()));
        float subAvoidDamping = 1.0f + MathHelper.Clamp(currentSpeed / 20.0f, 0.0f, 2.0f);
        _.TargetVelocity += (100.0f / subAvoidDamping) * Vector2.Normalize(diff) * distanceFactor * velocityFactor;
      }

      // Clamp velocity magnitude (X and Y components are clamped in the property setter)
      // float velMagnitude = _.TargetVelocity.Length();
      // if (velMagnitude > 100.0f)
      // {
      //   _.TargetVelocity *= 100.0f / velMagnitude;
      // }

#if CLIENT
        HintManager.OnAutoPilotPathUpdated(_);
#endif

      // Skip original method
      return false;
    }


    // https://github.com/evilfactory/LuaCsForBarotrauma/blob/ad837423a8d71666dc0a5621713e2ab1fe7e2802/Barotrauma/BarotraumaShared/SharedSource/Items/Components/Machines/Steering.cs#L347
    public static bool Steering_Update_Replace(Steering __instance, float deltaTime, Camera cam)
    {
      Steering _ = __instance;

      if (!_.searchedConnectedDockingPort)
      {
        _.FindConnectedDockingPort();
      }
      _.networkUpdateTimer -= deltaTime;
      if (_.unsentChanges)
      {
        if (_.networkUpdateTimer <= 0.0f)
        {
#if CLIENT
          if (GameMain.Client != null)
          {
              _.item.CreateClientEvent(_);
              _.correctionTimer = Steering.CorrectionDelay;
          }
#endif
#if SERVER
              _.item.CreateServerEvent(_);
#endif
          _.networkUpdateTimer = 0.1f;
          _.unsentChanges = false;
        }
      }

      _.controlledSub = _.item.Submarine;
      var sonar = _.item.GetComponent<Sonar>();
      if (sonar != null && sonar.UseTransducers)
      {
        _.controlledSub = sonar.ConnectedTransducers.Any() ? sonar.ConnectedTransducers.First().Item.Submarine : null;
      }

      if (!_.HasPower)
      {
        return false;
      }

      if (_.user != null && _.user.Removed)
      {
        _.user = null;
      }

      _.ApplyStatusEffects(ActionType.OnActive, deltaTime);

      float userSkill = 0.0f;
      if (_.user != null && _.controlledSub != null &&
          (_.user.SelectedItem == _.item || _.item.linkedTo.Contains(_.user.SelectedItem)))
      {
        userSkill = _.user.GetSkillLevel(Tags.HelmSkill) / 100.0f;
      }

      // override autopilot pathing while the AI rams, and go full speed ahead
      if (_.AIRamTimer > 0f && _.controlledSub != null)
      {
        _.AIRamTimer -= deltaTime;
        _.TargetVelocity = _.GetSteeringVelocity(_.AITacticalTarget, 0f);
      }
      else if (_.AutoPilot)
      {
        //signals override autopilot for a duration of one second
        if (_.lastReceivedSteeringSignalTime < Timing.TotalTime - 1)
        {
          _.UpdateAutoPilot(deltaTime);
          float throttle = 1.0f;
          if (_.controlledSub != null)
          {
            throttle = MathHelper.Clamp(Vector2.Dot(_.controlledSub.Velocity, _.TargetVelocity) / 100.0f, 0.0f, 1.0f);
          }
          float maxSpeed = MathHelper.Lerp(AutoPilotMaxSpeed, AIPilotMaxSpeed, userSkill) * 100.0f;
          _.TargetVelocity = _.TargetVelocity.ClampLength(MathHelper.Lerp(100.0f, maxSpeed, throttle));
        }
      }
      else
      {
        _.showIceSpireWarning = false;
        if (_.user != null && _.user.Info != null &&
            _.user.SelectedItem == _.item)
        {
          _.IncreaseSkillLevel(_.user, deltaTime);
        }

        Vector2 velocityDiff = _.steeringInput - _.targetVelocity;
        if (velocityDiff != Vector2.Zero)
        {
          if (_.steeringAdjustSpeed >= 0.99f)
          {
            _.TargetVelocity = _.steeringInput;
          }
          else
          {
            float steeringChange = 1.0f / (1.0f - _.steeringAdjustSpeed);
            steeringChange *= steeringChange * 10.0f;

            _.TargetVelocity += Vector2.Normalize(velocityDiff) *
                Math.Min(steeringChange * deltaTime, velocityDiff.Length());
          }
        }
      }

      float velX = _.targetVelocity.X;
      if (_.controlledSub != null && _.controlledSub.FlippedX) { velX *= -1; }
      _.item.SendSignal(new Signal(velX.ToString(CultureInfo.InvariantCulture), sender: _.user), "velocity_x_out");

      float velY = MathHelper.Lerp((_.neutralBallastLevel * 100 - 50) * 2, -100 * Math.Sign(_.targetVelocity.Y), Math.Abs(_.targetVelocity.Y) / 100.0f);
      _.item.SendSignal(new Signal(velY.ToString(CultureInfo.InvariantCulture), sender: _.user), "velocity_y_out");

      // converts the controlled sub's velocity to km/h and sends it. 
      if (_.controlledSub is { } sub)
      {
        _.item.SendSignal(new Signal((ConvertUnits.ToDisplayUnits(sub.Velocity.X * Physics.DisplayToRealWorldRatio) * 3.6f).ToString("0.0000", CultureInfo.InvariantCulture), sender: _.user), "current_velocity_x");
        _.item.SendSignal(new Signal((ConvertUnits.ToDisplayUnits(sub.Velocity.Y * Physics.DisplayToRealWorldRatio) * -3.6f).ToString("0.0000", CultureInfo.InvariantCulture), sender: _.user), "current_velocity_y");

        Vector2 pos = new Vector2(sub.WorldPosition.X * Physics.DisplayToRealWorldRatio, sub.RealWorldDepth);
        if (sonar != null && sonar.UseTransducers && sonar.CenterOnTransducers && sonar.ConnectedTransducers.Any())
        {
          pos = Vector2.Zero;
          foreach (var connectedTransducer in sonar.ConnectedTransducers)
          {
            pos += connectedTransducer.Item.WorldPosition;
          }
          pos /= sonar.ConnectedTransducers.Count();
          pos = new Vector2(
              pos.X * Physics.DisplayToRealWorldRatio,
              Level.Loaded?.GetRealWorldDepth(pos.Y) ?? (-pos.Y * Physics.DisplayToRealWorldRatio));
        }

        _.item.SendSignal(new Signal(pos.X.ToString("0.0000", CultureInfo.InvariantCulture), sender: _.user), "current_position_x");
        _.item.SendSignal(new Signal(pos.Y.ToString("0.0000", CultureInfo.InvariantCulture), sender: _.user), "current_position_y");
      }

      // if our tactical AI pilot has left, revert back to maintaining position
      if (_.navigateTactically && (_.user == null || _.user.SelectedItem != _.item))
      {
        _.navigateTactically = false;
        _.AIRamTimer = 0f;
        _.SetMaintainPosition();
      }

      // Skip original method
      return false;
    }

    // https://github.com/evilfactory/LuaCsForBarotrauma/blob/e902ba673ded233432fbfc00d890983c366880c1/Barotrauma/BarotraumaShared/SharedSource/Items/Components/Machines/Steering.cs#L634
    public static bool Steering_UpdatePath_Replace(Steering __instance)
    {
      Steering _ = __instance;

      if (Level.Loaded == null)
      {
        return false;
      }

      if (_.pathFinder == null)
      {
        _.pathFinder = new PathFinder(WayPoint.WayPointList, false)
        {
          GetNodePenalty = _.GetNodePenalty
        };
      }

      // Default to the end of the level just in case we fail to set a target.
      Vector2 target = ConvertUnits.ToSimUnits(Level.Loaded.EndExitPosition);
      bool targetSet = false;

      if (_.navigateTactically)
      {
        target = ConvertUnits.ToSimUnits(_.AITacticalTarget);
      }
      else if (_.LevelEndSelected)
      {
        target = ConvertUnits.ToSimUnits(Level.Loaded.EndExitPosition);
      }
      else
      {
        // Do people even use the start of the level as an autopilot target?
        // I'm assuming not, hence why I'm just replacing the start of level setting with mission target pathing.
        //target = ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition); // original line

        Vector2 newTarget = Vector2.Zero;
        float newDist = float.MaxValue;

        if (GameMain.GameSession != null)
        {
          foreach (var mission in GameMain.GameSession.Missions)
          {
            if (mission is Barotrauma.MineralMission || mission is Barotrauma.NestMission)
            {
              // Set target to cave entrance by looping through all caves and grabbing the first one that is indicated as a mission target.
              foreach (var cave in Level.Loaded.Caves)
              {
                // No mission targets on this cave, so immediately go to the next one.
                if (cave.MissionsToDisplayOnSonar.None()) continue;

                // Determine the closest cave target to the level start while iterating through the caves.
                Vector2 newCaveTarget = ConvertUnits.ToSimUnits(cave.StartPos.ToVector2());
                float newCaveDist = Vector2.DistanceSquared(newCaveTarget, ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition));

                if (newCaveDist < newDist)
                {
                  newTarget = newCaveTarget;
                  newDist = newCaveDist;
                }
              }

              // This handles the case where you have a mineral mission that isn't located within a cave.
              if (newTarget == Vector2.Zero && mission.SonarLabels.Any())
              {
                var sonarLabel = mission.SonarLabels.FirstOrDefault();                
                newTarget = ConvertUnits.ToSimUnits(sonarLabel.Position);
                newDist = Vector2.DistanceSquared(newTarget, ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition));
              }
            }
            else if (mission.SonarLabels.Any()) // Set new target to be the first sonar label position for the mission
            {
              var sonarLabel = mission.SonarLabels.FirstOrDefault();
              newTarget = ConvertUnits.ToSimUnits(sonarLabel.Position);
              newDist = Vector2.DistanceSquared(newTarget, ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition));
            }

            // If we've already retrieved the salvage (i.e. it is in the submarine), ignore this mission and don't set a target from it.
            if (mission is Barotrauma.SalvageMission && !((SalvageMission)mission).AnyTargetNeedsToBeRetrievedToSub)
            {
              // Make sure to clear the target if this was the most recently tracked one, so we don't keep trying to path to it.
              if (target == newTarget)
              {
                target = Vector2.Zero;
                targetSet = false;
              }
              continue;
            }

            float oldDist = Vector2.DistanceSquared(target, ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition));
#if DEBUG
            Log($"[AdvancedAutopilot] Steering_UpdatePath_Replace: mission is {mission}, mission target dist is {newDist}, old target dist is {oldDist}");
#endif

            // If we've picked a target already, compare the distance betwen the new target and the old one, and pick whichever is closer to the level start position.
            if (targetSet)
            {
              if (newDist < oldDist)
              {
                target = newTarget;
              }
            }
            else if (newTarget != Vector2.Zero)
            {
              target = newTarget;
              targetSet = true;
            }
          }
        }
      }

#if DEBUG
      Log($"[AdvancedAutopilot] Steering_UpdatePath_Replace: target set to {target}, level start pos is {ConvertUnits.ToSimUnits(Level.Loaded.StartExitPosition)}, level end pos is {ConvertUnits.ToSimUnits(Level.Loaded.EndExitPosition)}");
#endif
      SteeringPath rawPath = _.pathFinder.FindPath(ConvertUnits.ToSimUnits(_.controlledSub == null ? _.item.WorldPosition : _.controlledSub.WorldPosition), target, errorMsgStr: "(Autopilot, target: " + target + ")");
      _.steeringPath = SimplifyPath(rawPath);

      // Skip original method
      return false;
    }

    private static Vector2 smoothedVelocity = Vector2.Zero;
    private const float SmoothFactor = 0.05f;
    private const float CollinearDotThreshold = 0.85f;

    /// <summary>
    /// 路径简化：合并共线段，只保留转弯点，减少不必要的转向动作
    /// </summary>
    private static SteeringPath SimplifyPath(SteeringPath path)
    {
        if (path?.Nodes == null || path.Nodes.Count < 3) return path;

        var simplified = new List<WayPoint> { path.Nodes[0] };

        for (int i = 1; i < path.Nodes.Count - 1; i++)
        {
            Vector2 prev = path.Nodes[i - 1].WorldPosition;
            Vector2 curr = path.Nodes[i].WorldPosition;
            Vector2 next = path.Nodes[i + 1].WorldPosition;

            Vector2 dir1 = Vector2.Normalize(curr - prev);
            Vector2 dir2 = Vector2.Normalize(next - curr);

            // 方向变化 > ~10° 时保留该节点作为转弯点
            if (Vector2.Dot(dir1, dir2) < CollinearDotThreshold)
            {
                simplified.Add(path.Nodes[i]);
            }
        }

        simplified.Add(path.Nodes[path.Nodes.Count - 1]);

        var newPath = new SteeringPath();
        foreach (var wp in simplified)
        {
            newPath.AddNode(wp);
        }
        return newPath;
    }
  }
}