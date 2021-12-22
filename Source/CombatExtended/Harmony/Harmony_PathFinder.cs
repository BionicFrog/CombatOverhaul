﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.UI;
using Verse;
using Verse.AI;

namespace CombatExtended.HarmonyCE
{
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPath), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode), typeof(PathFinderCostTuning) })]
    internal static class Harmony_PathFinder_FindPath
    {        
        private static Pawn pawn;
        private static Map map;
        private static PathFinder instance;
        private static LightingTracker lightingTracker;                
        private static AvoidanceTracker.AvoidanceReader avoidanceReader;
        private static SightTracker.SightReader sightReader;
        private static AvoidanceTracker avoidanceTracker;
        private static bool crouching;        
        private static bool raiders;
        private static bool tpsLow;
        private static int counter;
        private static float tpsLevel;        
        private static float visibilityAtDest;        
        private static float factionMultiplier = 1.0f;        

        internal static bool Prefix(PathFinder __instance, ref PawnPath __result, IntVec3 start, LocalTargetInfo dest, TraverseParms traverseParms, PathEndMode peMode, out bool __state)
        {
            if (traverseParms.pawn != null && traverseParms.pawn.Faction != null && (traverseParms.pawn.RaceProps.Humanlike || traverseParms.pawn.RaceProps.IsMechanoid || traverseParms.pawn.RaceProps.Insect))
            {
                // prepare the performance parameters.
                tpsLevel = PerformanceTracker.TpsLevel;
                tpsLow = PerformanceTracker.TpsCriticallyLow;

                // prepare the modifications
                instance = __instance;
                map = __instance.map;
                pawn = traverseParms.pawn;

                // fix for player pawns and drafted pawns 
                factionMultiplier = pawn.Faction.IsPlayer ? (pawn.Drafted ? 0.25f : 0.5f) : 1.0f;
                // retrive CE elements
                avoidanceTracker = pawn.Map.GetAvoidanceTracker();
                lightingTracker = map.IsNightTime() ? map.GetLightingTracker() : null;
                avoidanceTracker.TryGetReader(pawn, out avoidanceReader);
                pawn.Map.GetSightTracker().TryGetReader(pawn, out sightReader);

                // get the visibility at the destination
                if (sightReader != null)
                {
                    visibilityAtDest = sightReader.GetVisibility(dest.Cell);
                    Verb verb = pawn.GetWeaponVerbWithFallback();
                    if (verb != null)
                    {
                        if (verb.verbProps.range > 20)
                        {
                            visibilityAtDest *= 0.25f;
                        }
                        else if (verb.verbProps.range > 10)
                        {
                            visibilityAtDest *= 0.425f;
                        }
                        else
                        {
                            visibilityAtDest *= 0.675f;
                        }
                    }
                    else
                    {
                        visibilityAtDest *= 0.50f;
                    }
                }
                // get wether this is a raider
                raiders = pawn.Faction?.HostileTo(Faction.OfPlayerSilentFail) ?? true;

                // Run normal if we're not being suppressed, running for cover, crouch-walking or not actually moving to another cell
                CompSuppressable comp = pawn?.TryGetCompFast<CompSuppressable>();
                if (comp == null || !comp.isSuppressed || comp.IsCrouchWalking || pawn.CurJob?.def == CE_JobDefOf.RunForCover || start == dest.Cell && peMode == PathEndMode.OnCell)
                {
                    __state = true;
                    crouching = comp?.IsCrouchWalking ?? false;
                    counter = 0;                   
                    return true;
                }
                //
                // Make all destinations unreachable
                __state = false;
                __result = PawnPath.NotFound;
                return false;
            }
            __state = false;
            Reset();            
            return true;            
        }        

        public static void Postfix(PathFinder __instance, PawnPath __result, bool __state)
        {
            if (__state)
            {              
                if (avoidanceTracker != null)
                    avoidanceTracker.Notify_PathFound(pawn, __result);
            }
            Reset();
        }

        public static void Reset()
        {
            avoidanceTracker = null;
            avoidanceReader = null;
            lightingTracker = null;
            sightReader = null;
            counter = 0;
            instance = null;
            visibilityAtDest = 0f;
            map = null;                   
            pawn = null;                        
        }

        /*
         * Search for the vairable that is initialized by the value from the avoid grid or search for
         * ((i > 3) ? num9 : num8) + num15;
         *          
         */
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> codes = instructions.ToList();
            bool finished = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!finished)
                {
                    if (codes[i].opcode == OpCodes.Stloc_S && codes[i].operand is LocalBuilder builder1 && builder1.LocalIndex == 46)
                    {
                        finished = true;
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 43).MoveLabelsFrom(codes[i]).MoveBlocksFrom(codes[i]);
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 3);
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 29);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Harmony_PathFinder_FindPath), nameof(Harmony_PathFinder_FindPath.GetCostOffsetAt)));
                        yield return new CodeInstruction(OpCodes.Add);
                    }
                }
                yield return codes[i];
            }
            if (finished)
                Log.Message("CE: Patched pather!");
        }

        private static int GetCostOffsetAt(int index, int parentIndex, int openNum)
        {
            if (map != null)
            {
                var value = 0;
                if (sightReader != null)
                {                    
                    var visibility = sightReader.GetVisibility(index);
                    if (visibility > visibilityAtDest)
                        value += (int) (visibility * 45);
                }
                if (value > 0 && !tpsLow)
                {
                    if (avoidanceReader != null)
                        value += (int)(avoidanceReader.GetPathing(index) * 25 + avoidanceReader.GetDanger(index) * 10);
                    if (lightingTracker != null)
                        value += (int)(lightingTracker.CombatGlowAt(map.cellIndices.IndexToCell(index)) * 25f);
                }
                else
                {
                    if (avoidanceReader != null)
                        value += (int)(avoidanceReader.GetPathing(index) * 15);
                }
                if (value > 10f)
                {
                    counter++;
                    //
                    // TODO make this into a maxcost -= something system
                    var l1 = 450 * (1f - Mathf.Lerp(0f, 0.75f, counter / (openNum + 1f))) * (1f - Mathf.Min(openNum, 5000) / (7500));                    
                    var l2 = 250 * (1f - Mathf.Clamp01(PathFinder.calcGrid[parentIndex].knownCost / 2500));
                    // we use this so the game doesn't die   
                    return (int)(Mathf.Min(value, l1 + l2) * factionMultiplier * tpsLevel);
                }
            }            
            return 0;
        }       
    }
}