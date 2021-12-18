﻿using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended.AI;
using CombatExtended.Utilities;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CombatExtended
{
    public static class SuppressionUtility
    {
        public const float maxCoverDist = 10f; //Maximum distance to run for cover to

        private static AvoidanceTracker avoidanceTracker;
        private static AvoidanceTracker.AvoidanceReader avoidanceReader;
        private static LightingTracker lightingTracker;        
        private static SightTracker.SightReader sightReader;
        private static List<CompProjectileInterceptor> interceptors;

        public static bool TryRequestHelp(Pawn pawn)
        {
            Map map = pawn.Map;
            float curLevel = pawn.TryGetComp<CompSuppressable>().CurrentSuppression;
            ThingWithComps grenade = null;
            foreach (Pawn other in pawn.Position.PawnsInRange(map, 8))
            {
                if (other.Faction != pawn.Faction)
                    continue;
                if (other.jobs?.curDriver is IJobDriver_Tactical)
                    continue;
                if (!(other.TryGetComp<CompInventory>()?.TryFindSmokeWeapon(out grenade) ?? false))
                    continue;
                CompSuppressable otherSup = other.TryGetComp<CompSuppressable>();
                if ((otherSup?.isSuppressed ?? true) || otherSup.CurrentSuppression > curLevel)
                    continue;
                if (!GenSight.LineOfSight(pawn.Position, other.Position, map))
                    continue;
                Job job = JobMaker.MakeJob(CE_JobDefOf.OpportunisticAttack, grenade, pawn.Position);
                job.maxNumStaticAttacks = 1;
                other.jobs.StartJob(job, JobCondition.InterruptForced);
                return true;
            }
            return false;
        }

        public static Job GetRunForCoverJob(Pawn pawn)
        {
            //Calculate cover position
            CompSuppressable comp = pawn.TryGetComp<CompSuppressable>();
            if (comp == null)
            {
                return null;
            }            
            float distToSuppressor = (pawn.Position - comp.SuppressorLoc).LengthHorizontal;
            IntVec3 coverPosition;
            //Try to find cover position to move up to
            if (!GetCoverPositionFrom(pawn, comp.SuppressorLoc, maxCoverDist, out coverPosition))
            {
                return null;
            }
            //Sanity check
            if (pawn.Position.Equals(coverPosition))
            {
                return null;
            }
            //Tell pawn to move to position
            var job = JobMaker.MakeJob(CE_JobDefOf.RunForCover, coverPosition);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.playerForced = true;
            return job;
        }

        public static Job GetRunForCoverJob(Pawn pawn, IntVec3 from)
        {            
            float distToSuppressor = (pawn.Position - from).LengthHorizontal;
            IntVec3 coverPosition;
            // Try to find cover position to move up to
            if (!GetCoverPositionFrom(pawn, from, maxCoverDist, out coverPosition))
            {
                return null;
            }
            // Sanity check
            if (pawn.Position.Equals(coverPosition))
            {
                return null;
            }
            // Tell pawn to move to position
            var job = JobMaker.MakeJob(CE_JobDefOf.RunForCover, coverPosition);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.playerForced = true;
            return job;
        }

        private static bool GetCoverPositionFrom(Pawn pawn, IntVec3 fromPosition, float maxDist, out IntVec3 coverPosition)
        {
            List<IntVec3> cellList = new List<IntVec3>(GenRadial.RadialCellsAround(pawn.Position, maxDist, true));
            IntVec3 bestPos = pawn.Position;
            interceptors = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor).Select(t => t.TryGetComp<CompProjectileInterceptor>()).ToList();
            lightingTracker = pawn.Map.GetLightingTracker();
            avoidanceTracker = pawn.Map.GetAvoidanceTracker();
            avoidanceTracker.TryGetAvoidanceReader(pawn, out avoidanceReader);
            pawn.GetSightReader(out sightReader);

            float moveSpeed = Mathf.Max(pawn.GetStatValue(StatDefOf.MoveSpeed), 0.5f);
            float baseVisibility = sightReader?.GetVisibility(pawn.Position) ?? 0f;
            float baseDanger = avoidanceReader?.GetDanger(pawn.Position) ?? 0f;
            float bestRating = GetCellCoverRatingForPawn(pawn, pawn.Position, fromPosition) - GetCellPathCost(pawn.Position, moveSpeed, 0, 0);
            if (bestRating <= 0)
            {                       
                pawn.Map.GetComponent<MapCompCE>().Flooder.Flood(pawn.Position,
                (cell, parent, cost) =>
                {
                    float cellRating = GetCellCoverRatingForPawn(pawn, cell, fromPosition) - cost;
                    if (cellRating > bestRating)
                    {
                        bestRating = cellRating;
                        bestPos = cell;
                    }
                    //
                    //pawn.Map.debugDrawer.FlashCell(cell, cost, $"{Math.Round(cost, 1)} {Math.Round(cellRating, 1)}", 20);
                },
                (cell) =>
                {                    
                    return GetCellPathCost(cell, moveSpeed, baseVisibility, baseDanger);
                }, maxDist: (int) maxDist);
            }
            coverPosition = bestPos;
            avoidanceTracker.Notify_CoverPositionSelected(pawn, bestPos);
            avoidanceReader = null;
            lightingTracker = null;
            sightReader = null;
            return bestRating >= 0;
        }

        private static float GetCellPathCost(IntVec3 cell, float movespeed, float baseVisibility, float baseDanger)
        {
            float cost = 0f;
            if (sightReader != null)
                cost += Mathf.Max(sightReader.GetVisibility(cell) - baseVisibility, 0f);
            if (avoidanceReader != null)
                cost += Mathf.Max(avoidanceReader.GetDanger(cell) - baseDanger, 0f) / movespeed;
            return cost;
        }

        private static float GetCellCoverRatingForPawn(Pawn pawn, IntVec3 cell, IntVec3 shooterPos)
        {
            //
            // Check for invalid locations            
            if (!cell.IsValid || !cell.Standable(pawn.Map) || !pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.Deadly) || cell.ContainsStaticFire(pawn.Map))
            {
                return -1f;
            }
            float cellRating = 0;            

            if (!GenSight.LineOfSight(shooterPos, cell, pawn.Map))
            {
                cellRating += 12;
            }
            else
            {
                //Check if cell has cover in desired direction
                Vector3 coverVec = (shooterPos - cell).ToVector3().normalized;
                IntVec3 coverCell = (cell.ToVector3Shifted() + coverVec).ToIntVec3();
                Thing cover = coverCell.GetCover(pawn.Map);
                cellRating += GetCoverRating(cover) * 5;
            }
            float visibilityRating = 0;
            if (sightReader != null)
                visibilityRating += sightReader.GetVisibility(cell) * 2;

            if (visibilityRating > 0f)
            {
                // Avoid bullets and other danger source
                cellRating -= visibilityRating;
                // Only apply this at night for performance reasons.
                if (lightingTracker.IsNight)
                    cellRating -= lightingTracker.CombatGlowAtFor(shooterPos, cell) * 2f;
            }
            if (avoidanceReader != null)
                cellRating -= Mathf.Min(avoidanceReader.GetDanger(cell) + avoidanceReader.GetProximity(cell), 8f);

            // better cover rating system
            float coverLOSRating = 0;
            foreach (IntVec3 pos in pawn.Map.PartialLineOfSights(cell, shooterPos))
            {
                if (cell == pawn.Position)
                    continue;
                Thing cover = pos.GetCover(pawn.Map);                
                if (cover == null)
                    continue;
                if (cover is Gas)
                    coverLOSRating += 5;
                else if (cover.def.Fillage == FillCategory.Partial) 
                    coverLOSRating += cover.def.category == ThingCategory.Plant ? 0.5f : 3;
            }
            cellRating += Mathf.Min(coverLOSRating, 25);

            //Check time to path to that location
            //if (!pawn.Position.Equals(cell))
            //{
            //    // float pathCost = pawn.Map.pathFinder.FindPath(pawn.Position, cell, TraverseMode.NoPassClosedDoors).TotalCost;
            //    float pathCost = Mathf.Abs(cell.x - pawn.Position.x) + 0.5f * Mathf.Abs(cell.z - pawn.Position.z);
            //    if (!GenSight.LineOfSight(pawn.Position, cell, pawn.Map))                
            //        pathCost *= 2; 
            //    cellRating = cellRating - pathCost;
            //}
            for (int i = 0; i < interceptors.Count; i++)
            {
                CompProjectileInterceptor interceptor = interceptors[i];
                if (interceptor.Active && interceptor.parent.Position.DistanceTo(cell) < interceptor.Props.radius)
                    cellRating += 10;
            }
            return cellRating;
        }

        private static float GetCoverRating(Thing cover)
        {
            if (cover == null) return 0;
            if (cover is Gas) return 0.8f;
            if (cover.def.category == ThingCategory.Plant) return cover.def.fillPercent; // Plant cover only has a random chance to block gunfire and is rated lower            
            return 1;
        }

        public static bool TryGetSmokeScreeningJob(Pawn pawn, IntVec3 suppressorLoc, out Job job)
        {
            job = null;
            ThingWithComps grenade = null;
            if (!pawn.TryGetComp<CompInventory>()?.TryFindSmokeWeapon(out grenade) ?? true)
                return false;
            var range = 5;
            var castTarget = pawn.Position;
            foreach (IntVec3 cell in GenSightCE.PartialLineOfSights(pawn, suppressorLoc))
            {
                if (cell.DistanceTo(pawn.Position) >= range)
                    break;
                castTarget = cell;
            }
            if (!castTarget.IsValid)
                castTarget = pawn.Position;
            job = JobMaker.MakeJob(CE_JobDefOf.OpportunisticAttack, grenade, castTarget);
            job.maxNumStaticAttacks = 1;
            return true;
        }

        public static List<MentalStateDef> GetPossibleBreaks(Pawn pawn)
        {
            var breaks = new List<MentalStateDef>();
            var traits = pawn.story.traits;

            // Panic break
            if (!(traits.HasTrait(TraitDefOf.Bloodlust)
                || traits.DegreeOfTrait(TraitDefOf.Nerves) > 0
                || traits.DegreeOfTrait((CE_TraitDefOf.Bravery)) > 1))
            {
                breaks.Add(pawn.IsColonist ? MentalStateDefOf.Wander_OwnRoom : MentalStateDefOf.PanicFlee);
                breaks.Add(CE_MentalStateDefOf.ShellShock);
            }

            // Attack break
            if (!(pawn.WorkTagIsDisabled(WorkTags.Violent)
                  || traits.DegreeOfTrait(TraitDefOf.Nerves) < 0
                  || traits.DegreeOfTrait(CE_TraitDefOf.Bravery) < 0))
            {
                breaks.Add(CE_MentalStateDefOf.CombatFrenzy);
            }

            return breaks;
        }
    }
}
