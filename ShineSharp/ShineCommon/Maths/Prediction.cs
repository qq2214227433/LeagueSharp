﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using LeagueSharp;
using LeagueSharp.Common;
using SharpDX;

namespace ShineCommon.Maths
{
    public static class Prediction
    {
        public struct EnemyData
        {
            public bool IsStopped;
            public List<Vector2> LastWaypoints;
            public int LastWaypointTick;
            public int StopTick;
            public float AvgTick;
            public int Count;
            public object m_lock;

            public EnemyData(List<Vector2> wp)
            {
                IsStopped = false;
                LastWaypoints = wp;
                LastWaypointTick = 0;
                StopTick = 0;
                AvgTick = 0;
                Count = 0;
                m_lock = new object();
            }
        }

        public static Dictionary<int, EnemyData> EnemyInfo = new Dictionary<int, EnemyData>();
        private static bool blInitialized;

        public static void Initialize()
        {
            foreach (Obj_AI_Hero enemy in HeroManager.Enemies)
                EnemyInfo.Add(enemy.NetworkId, new EnemyData(new List<Vector2>()));

            Obj_AI_Hero.OnNewPath += Obj_AI_Hero_OnNewPath;
            blInitialized = true;
        }

        public static Vector2 GetPrediction(Obj_AI_Hero target, Spell s, List<Vector2> path, float avgt, float movt, out HitChance hc)
        {
            if (!blInitialized)
                throw new Exception("Prediction is not initalized");

            float dist_target = ObjectManager.Player.ServerPosition.Distance(target.ServerPosition);
            Vector2 Vt = (path[path.Count - 1] - path[0]).Normalized() * target.MoveSpeed;
            Vector2 Vs = (target.ServerPosition.To2D() - ObjectManager.Player.ServerPosition.To2D()).Normalized() * s.Speed;
            Vector2 Vr = Vs - Vt;

            if (target.MovImmobileTime() > 200 || target.AvgMovChangeTime() == 0 || Utility.IsImmobileTarget(target))
            {
                hc = HitChance.Immobile;
                return target.ServerPosition.To2D() + target.Direction.To2D().Perpendicular() * s.Width / 2;
            }

            float flytime = 0f;
             
            if (s.Speed != 0)
            {
                flytime = dist_target / Vr.Length();

                if (path.Count > 5) //complicated movement
                    flytime = dist_target / s.Speed;
            }

            float t = flytime + s.Delay + Game.Ping / 1000f;
            float t_ms = t * 1000f;

            float distance = t * target.MoveSpeed;

            //can be improved by checking area of circle
            if (s.Type == SkillshotType.SkillshotCircle) //haven't tested yet.
                distance -= s.Width / 2;

            if (avgt - movt >= t_ms)
                hc = HitChance.VeryHigh;
            else if (avgt - movt >= t_ms * 0.5f)
                hc = HitChance.High;
            else if (avgt - movt >= t_ms && avgt / movt >= 1.5f)
                hc = HitChance.Medium;
            else
                hc = HitChance.Low;

            for (int i = 0; i < path.Count - 1; i++)
            {
                float d = path[i + 1].Distance(path[i]);
                if (distance == d)
                {
                    return path[i + 1];
                }
                else if (distance < d)
                {
                    return path[i] + distance * (path[i + 1] - path[i]).Normalized();
                }
                else distance -= d;
            }

            hc = HitChance.Impossible;
            return path[path.Count - 1];
        }

        public static bool CastWithMovementCheck(this Spell s, Obj_AI_Hero t, HitChance hc = HitChance.Medium, float filter_hppercent = 0, int min_hit = 1)
        {
            if (!blInitialized)
                throw new Exception("Prediction is not initalized");

            if (min_hit < 1)
                throw new ArgumentException("Minimum Hit Count Can't be less than 1 kappa");

            if (min_hit > 1)
            {
                return AoeCast(s, t, filter_hppercent, min_hit, hc);
            }

            if (Monitor.TryEnter(EnemyInfo[t.NetworkId].m_lock))
            {
                try
                {
                    HitChance predictedhc;
                    float avgt = t.AvgMovChangeTime();
                    float movt = t.LastMovChangeTime();
                    Vector2 pos = GetPrediction(t, s, t.GetWaypoints(), avgt, movt, out predictedhc);

                    if (pos.Distance(t.ServerPosition.To2D()) > s.Range)
                    {
                        Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                        return false;
                    }

                    //pos = pos + pos.Perpendicular() * s.Width / 2; //need moar test (for lineaar skillshots)
                    if (s.Collision && s.GetCollision(ObjectManager.Player.ServerPosition.To2D(), new List<Vector2> { pos }).Exists(q => q.IsEnemy)) //needs update
                    {
                        Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                        return false;
                    }


                    if (predictedhc >= hc)
                    {
                        s.Cast(pos);

                        return true;
                    }

                    Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                    return false;
                }
                finally
                {
                    Monitor.Exit(EnemyInfo[t.NetworkId].m_lock);
                }
            }

            return false;
        }

        private static bool AoeCast(Spell s, Obj_AI_Hero t, float filter_hppercent, int min_hit, HitChance hc = HitChance.Medium)
        {
            if (!blInitialized)
                throw new Exception("Prediction is not initalized");

            if (s.Type != SkillshotType.SkillshotLine) //just supports lineaar now
                return false;

            if (Monitor.TryEnter(EnemyInfo[t.NetworkId].m_lock))
            {
                try
                {
                    HitChance predictedhc;
                    float avgt = t.AvgMovChangeTime();
                    float movt = t.LastMovChangeTime();
                    Vector2 pos = Line.GetPrediction(t, s, t.GetWaypoints(), avgt, movt, filter_hppercent, min_hit, out predictedhc);

                    if (pos.Distance(t.ServerPosition.To2D()) > s.Range)
                    {
                        Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                        return false;
                    }

                    //pos = pos + pos.Perpendicular() * s.Width / 2; //need moar test (for lineaar skillshots)
                    if (s.Collision && s.GetCollision(ObjectManager.Player.ServerPosition.To2D(), new List<Vector2> { pos }).Exists(q => q.IsEnemy)) //needs update
                    {
                        Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                        return false;
                    }


                    if (predictedhc >= hc)
                    {
                        s.Cast(pos);

                        return true;
                    }

                    Monitor.Pulse(EnemyInfo[t.NetworkId].m_lock);
                    return false;
                }
                finally
                {
                    Monitor.Exit(EnemyInfo[t.NetworkId].m_lock);
                }
            }
            return false;
        }

        #region aeo
        //modified common prediction
        public static class Line
        {
            private static IEnumerable<Vector2> GetHits(Vector2 start, Vector2 end, double radius, List<Vector2> points)
            {
                return points.Where(p => p.Distance(start, end, true, true) <= radius * radius);
            }

            private static bool GetCandidates(Vector2 from, Vector2 to, float radius, float range, out Vector2[] vec)
            {
                var middlePoint = (from + to) / 2;
                var intersections = LeagueSharp.Common.Geometry.CircleCircleIntersection(
                    from, middlePoint, radius, from.Distance(middlePoint));

                if (intersections.Length > 1)
                {
                    var c1 = intersections[0];
                    var c2 = intersections[1];

                    c1 = from + range * (to - c1).Normalized();
                    c2 = from + range * (to - c2).Normalized();

                    vec = new[] { c1, c2 };
                    return true;
                }

                vec = new Vector2[] { };
                return false;
            }

            private static List<PossibleTarget> GetPossibleTargets(Obj_AI_Hero target, Spell s, Vector3 mypos, float filter_hppercent)
            {
                var result = new List<PossibleTarget>();
                var originalUnit = target;
                var enemies = HeroManager.Enemies.FindAll(h => h.NetworkId != originalUnit.NetworkId && h.IsValidTarget(s.Range, true, mypos) && h.Health / h.MaxHealth * 100 <= filter_hppercent);
                foreach (var enemy in enemies)
                {
                    HitChance hc;
                    var prediction = Prediction.GetPrediction(enemy, s, enemy.GetWaypoints(), enemy.AvgMovChangeTime(), enemy.LastMovChangeTime(), out hc);
                    if (hc >= HitChance.High)
                    {
                        result.Add(new PossibleTarget { Position = prediction, Unit = enemy });
                    }
                }
                return result;
            }



            public static Vector2 GetPrediction(Obj_AI_Hero t, Spell s, List<Vector2> path, float avgt, float movt, float filter_hppercent, int min_hit, out HitChance hc)
            {
                Vector2 castpos = Prediction.GetPrediction(t, s, path, avgt, movt, out hc);

                var posibleTargets = new List<PossibleTarget>
                {
                    new PossibleTarget { Position = t.ServerPosition.To2D(), Unit = t }
                };

                if (hc >= HitChance.Medium)
                {
                    //Add the posible targets  in range:
                    posibleTargets.AddRange(GetPossibleTargets(t, s, ObjectManager.Player.ServerPosition, filter_hppercent));
                    if (posibleTargets.Count < min_hit)
                    {
                        hc = HitChance.Impossible;
                        return castpos;
                    }
                }

                if (posibleTargets.Count > 1)
                {
                    var candidates = new List<Vector2>();
                    foreach (var target in posibleTargets)
                    {
                        Vector2[] v;
                        if(GetCandidates(ObjectManager.Player.ServerPosition.To2D(), target.Position, s.Width, s.Range, out v))
                            candidates.AddRange(v);
                    }

                    var bestCandidateHits = -1;
                    var bestCandidate = new Vector2();
                    var bestCandidateHitPoints = new List<Vector2>();
                    var positionsList = posibleTargets.Select(p => p.Position).ToList();

                    foreach (var candidate in candidates)
                    {
                        if (GetHits(ObjectManager.Player.ServerPosition.To2D(), candidate, s.Width, new List<Vector2> { posibleTargets[0].Position }).Count() == 1)
                        {
                            var hits = GetHits(ObjectManager.Player.ServerPosition.To2D(), candidate, s.Width, positionsList).ToList();
                            var hitsCount = hits.Count;
                            if (hitsCount >= bestCandidateHits)
                            {
                                bestCandidateHits = hitsCount;
                                bestCandidate = candidate;
                                bestCandidateHitPoints = hits.ToList();
                            }
                        }
                    }

                    if (bestCandidateHits > 1)
                    {
                        float maxDistance = -1;
                        Vector2 p1 = new Vector2(), p2 = new Vector2();

                        //Center the position
                        for (var i = 0; i < bestCandidateHitPoints.Count; i++)
                        {
                            for (var j = 0; j < bestCandidateHitPoints.Count; j++)
                            {
                                var startP = ObjectManager.Player.ServerPosition.To2D();
                                var endP = bestCandidate;
                                var proj1 = positionsList[i].ProjectOn(startP, endP);
                                var proj2 = positionsList[j].ProjectOn(startP, endP);
                                var dist = Vector2.DistanceSquared(bestCandidateHitPoints[i], proj1.LinePoint) +
                                           Vector2.DistanceSquared(bestCandidateHitPoints[j], proj2.LinePoint);
                                if (dist >= maxDistance &&
                                    (proj1.LinePoint - positionsList[i]).AngleBetween(
                                        proj2.LinePoint - positionsList[j]) > 90)
                                {
                                    maxDistance = dist;
                                    p1 = positionsList[i];
                                    p2 = positionsList[j];
                                }
                            }
                        }

                        if (bestCandidateHits < min_hit)
                        {
                            hc = HitChance.Impossible;
                            return castpos;
                        }

                        return (p1 + p2) * 0.5f;
                    }
                }

                hc = HitChance.Impossible;
                return castpos;
            }
        }

        private class PossibleTarget
        {
            public Vector2 Position;
            public Obj_AI_Base Unit;
        }

        #endregion

        static void Obj_AI_Hero_OnNewPath(Obj_AI_Base sender, GameObjectNewPathEventArgs args)
        {
            if (!sender.IsEnemy || !sender.IsChampion())
                return;

            EnemyData enemy = EnemyInfo[sender.NetworkId];

            lock (enemy.m_lock)
            {
                if (args.Path.Length < 2)
                {
                    if (!enemy.IsStopped)
                    {
                        enemy.StopTick = Environment.TickCount;
                        enemy.LastWaypointTick = Environment.TickCount;
                        enemy.IsStopped = true;
                        enemy.Count = 0;
                        enemy.AvgTick = 0;
                    }
                }
                else
                {
                    List<Vector2> wp = args.Path.Select(p => p.To2D()).ToList();
                    if (!enemy.LastWaypoints.SequenceEqual(wp))
                    {
                        if (!enemy.IsStopped)
                            enemy.AvgTick = (enemy.Count * enemy.AvgTick + (Environment.TickCount - enemy.LastWaypointTick)) / ++enemy.Count;
                        enemy.LastWaypointTick = Environment.TickCount;
                        enemy.IsStopped = false;
                        enemy.LastWaypoints = wp;
                    }
                }

                EnemyInfo[sender.NetworkId] = enemy;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MovImmobileTime(this Obj_AI_Hero e)
        {
            return EnemyInfo[e.NetworkId].IsStopped ? Environment.TickCount - EnemyInfo[e.NetworkId].StopTick : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastMovChangeTime(this Obj_AI_Hero e)
        {
            return Environment.TickCount - EnemyInfo[e.NetworkId].LastWaypointTick;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AvgMovChangeTime(this Obj_AI_Hero e)
        {
            return EnemyInfo[e.NetworkId].AvgTick;
        }
    }
}