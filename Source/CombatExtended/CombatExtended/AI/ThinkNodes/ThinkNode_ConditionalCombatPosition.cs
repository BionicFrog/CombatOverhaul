﻿using System;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CombatExtended.AI
{
	public class ThinkNode_ConditionalCombatPosition : ThinkNode_Conditional
	{
        /// <summary>
        /// The visibile enemy count range
        /// </summary>
        public FloatRange visibileEnemies = new FloatRange(0, 0);       
        /// <summary>
        /// Wether cover is required
        /// </summary>
        public bool requiresCover = false;
        /// <summary>
        /// Wether to use visibility for the evaluation.
        /// </summary>
        public bool useVisibility = false;
        
        public override bool Satisfied(Pawn pawn)
        {
            pawn.GetSightReader(out SightTracker.SightReader reader);            
            if (reader == null)
                return false;
            IntVec3 pos = pawn.Position;
            if (!visibileEnemies.Includes(!useVisibility ? (int)reader.GetEnemies(pos) : Mathf.CeilToInt(reader.GetVisibility(pos))))
                return false;
            if (!requiresCover)
                return true;
            // use the flow vector to determine where the cover should be at
            Vector2 enemyDir = (-1f * reader.GetDirection(pos)).normalized * 7;

            Map map = pawn.Map;
            // prepare out fillage cache system.
            WallGrid grid = map.GetWallGrid();
            foreach(IntVec3 cell in GenSight.PointsOnLineOfSight(pos, pos + new IntVec3((int)enemyDir.x, 0, (int)enemyDir.y)))
            {
                if (!cell.InBounds(map))
                    continue;
                if (grid.GetFillCategory(cell) != FillCategory.None)
                    return true;
            }
            return false;
        }
    }
}
