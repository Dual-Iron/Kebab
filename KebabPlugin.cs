using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RWCustom;
using StaticTables;
using System;

namespace Kebab
{
    struct PhysObjData : IWeakData<PhysicalObject>
    {
        public const int noLayer = int.MinValue;

        public int layer;
        public float range;
        public float angle;

        void IWeakData<PhysicalObject>.Construct(PhysicalObject owner)
        {
            layer = noLayer;
        }
        void IWeakData<PhysicalObject>.Destruct() { }
    }

    struct SpearData : IWeakData<Spear>
    {
        public FContainer container;

        void IWeakData<Spear>.Construct(Spear key) { }
        void IWeakData<Spear>.Destruct() { }
    }

    [BepInPlugin("com.github.dual.kebab", "Kebab", "1.0.0")]
    internal class KebabPlugin : BaseUnityPlugin
    {
        private static AbstractPhysicalObject.ImpaledOnSpearStick GetImpaled(PhysicalObject self)
        {
            if (self?.abstractPhysicalObject is null)
            {
                return null;
            }

            foreach (var obj in self.abstractPhysicalObject.stuckObjects)
            {
                if (obj is AbstractPhysicalObject.ImpaledOnSpearStick i && i.B == self.abstractPhysicalObject)
                {
                    return i;
                }
            }

            return null;
        }

        private static bool IsKebabbable(PhysicalObject obj)
        {
            try
            {
                return obj is IPlayerEdible e && e.Edible && obj.TotalMass < 0.4f && GetImpaled(obj) is null;
            }
            catch
            {
                return false;
            }
        }

        private static void TryImpale(Spear self, PhysicalObject obj, int chunk)
        {
            int numImpaled = 0;
            int posOnSpear = 0;

            for (int i = 0; i < self.abstractPhysicalObject.stuckObjects.Count; i++)
                if (self.abstractPhysicalObject.stuckObjects[i] is AbstractPhysicalObject.ImpaledOnSpearStick o)
                {
                    if (o.ObjectOnSpear == obj.abstractPhysicalObject)
                    {
                        return;
                    }
                    if (o.onSpearPosition == posOnSpear)
                    {
                        posOnSpear++;
                    }
                    numImpaled++;
                }

            if (numImpaled > 5 || posOnSpear >= 5)
                return;

            obj.AllGraspsLetGoOfThisObject(true);

            new AbstractPhysicalObject.ImpaledOnSpearStick(self.abstractPhysicalObject, obj.abstractPhysicalObject, chunk, posOnSpear);
        }

        public void OnEnable()
        {
            On.Spear.TryImpaleSmallCreature += Spear_TryImpaleSmallCreature;

            On.BodyChunk.HardSetPosition += BodyChunk_HardSetPosition;
            On.VultureGrub.Update += VultureGrub_Update;

            On.PlayerGraphics.PlayerObjectLooker.HowInterestingIsThisObject += PlayerObjectLooker_HowInterestingIsThisObject;
            On.AbstractPhysicalObject.AbstractObjectStick.Deactivate += AbstractObjectStick_Deactivate;
            On.Weapon.AddToContainer += Weapon_AddToContainer;
            On.Room.AddObject += Room_AddObject;
            On.PhysicalObject.Update += PhysicalObject_Update;
            On.Spear.HitSomethingWithoutStopping += Spear_HitSomethingWithoutStopping;
            On.Weapon.HitThisObject += FixDuplicateStuckObjects;
            IL.Player.PickupCandidate += Player_PickupCandidate;
            IL.Spear.HitSomething += Spear_HitSomething;

            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithTerrain"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithSlopes"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithObjects"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_GoThroughFloors"), blockSetter2).Apply();
        }

        // NOTE: THIS DOES NOT CALL ORIG
        private void Spear_TryImpaleSmallCreature(On.Spear.orig_TryImpaleSmallCreature orig, Spear self, Creature smallCrit)
        {
            TryImpale(self, smallCrit, 0);
        }

        #region FIX VULTURE GRUB SPASM
        private bool grub;

        private void BodyChunk_HardSetPosition(On.BodyChunk.orig_HardSetPosition orig, BodyChunk self, UnityEngine.Vector2 newPos)
        {
            if (!grub)
                orig(self, newPos);
        }


        private void VultureGrub_Update(On.VultureGrub.orig_Update orig, VultureGrub self, bool eu)
        {
            grub = true;
            orig(self, eu);
            grub = false;
        }
        #endregion

        private float PlayerObjectLooker_HowInterestingIsThisObject(On.PlayerGraphics.PlayerObjectLooker.orig_HowInterestingIsThisObject orig, object self, PhysicalObject obj)
        {
            if (GetImpaled(obj) is not null)
            {
                return 0;
            }
            return orig(self, obj);
        }

        private void AbstractObjectStick_Deactivate(On.AbstractPhysicalObject.AbstractObjectStick.orig_Deactivate orig, AbstractPhysicalObject.AbstractObjectStick self)
        {
            if (self is AbstractPhysicalObject.ImpaledOnSpearStick impaleStick && impaleStick.Spear.realizedObject is Spear spear && impaleStick.ObjectOnSpear.realizedObject is PhysicalObject o)
            {
                if (spear.Data().Get<SpearData>().container is FContainer)
                {
                    var drawable = o as IDrawable ?? o.graphicsModule;
                    if (drawable != null)
                        foreach (var camera in spear.room.game.cameras)
                        {
                            camera.MoveObjectToContainer(drawable, null);
                        }
                }
            }
            orig(self);
        }

        private void Weapon_AddToContainer(On.Weapon.orig_AddToContainer orig, Weapon self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContatiner)
        {
            if (self is Spear s)
            {
                ref var data = ref s.Data().Get<SpearData>();
                data.container ??= new();
                data.container.RemoveFromContainer();

                orig(self, sLeaser, rCam, data.container);

                (newContatiner ?? rCam.ReturnFContainer("Items")).AddChild(data.container);
            }
            else orig(self, sLeaser, rCam, newContatiner);
        }

        private void Room_AddObject(On.Room.orig_AddObject orig, Room self, UpdatableAndDeletable obj)
        {
            // TODO: Note that this fixes the double-speed bug when traveling between rooms
            if (self.updateList.IndexOf(obj) == -1)
            {
                orig(self, obj);
            }
        }

        private void PhysicalObject_Update(On.PhysicalObject.orig_Update orig, PhysicalObject self, bool eu)
        {
            ref var data = ref self.Data().Get<PhysObjData>();
            var impaled = GetImpaled(self);
            if (impaled != null && impaled.A.Room.index == impaled.B.Room.index && impaled.A.realizedObject is Spear s && impaled.B.realizedObject == self && !s.slatedForDeletetion)
            {
                if (s.Data().Get<SpearData>().container is FContainer container)
                {
                    var drawable = self as IDrawable ?? self.graphicsModule;
                    if (drawable != null)
                        foreach (var camera in s.room.game.cameras)
                        {
                            camera.MoveObjectToContainer(drawable, container);
                        }
                }

                if (data.layer == PhysObjData.noLayer)
                {
                    data.layer = self.collisionLayer;
                    data.range = self.collisionRange;

                    switch (self)
                    {
                        case DangleFruit selfCast:  data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                        case EggBugEgg selfCast:    data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                        case JellyFish selfCast:    data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                        case KarmaFlower selfCast:  data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                        case SlimeMold selfCast:    data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                        case Mushroom selfCast:     data.angle = Custom.VecToDeg(selfCast.rotation) - Custom.VecToDeg(s.rotation); break;
                    }
                }

                self.ChangeCollisionLayer(0);
                self.collisionRange = float.NegativeInfinity;

                for (int i = 0; i < self.bodyChunks.Length; i++)
                {
                    self.bodyChunks[i].goThroughFloors = true;
                    self.bodyChunks[i].collideWithObjects = false;
                    self.bodyChunks[i].collideWithSlopes = false;
                    self.bodyChunks[i].collideWithTerrain = false;
                }

                orig(self, eu);

                self.ChangeCollisionLayer(0);
                self.collisionRange = float.NegativeInfinity;

                for (int i = 0; i < self.bodyChunks.Length; i++)
                {
                    self.bodyChunks[i].goThroughFloors = true;
                    self.bodyChunks[i].collideWithObjects = false;
                    self.bodyChunks[i].collideWithSlopes = false;
                    self.bodyChunks[i].collideWithTerrain = false;
                }

                self.bodyChunks[impaled.chunk].pos = s.firstChunk.pos + s.rotation * Custom.LerpMap(impaled.onSpearPosition, 0f, 4f, 15f, -15f);

                switch (self)
                {
                    case DangleFruit selfCast:  selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                    case EggBugEgg selfCast:    selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                    case JellyFish selfCast:    selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                    case KarmaFlower selfCast:  selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                    case SlimeMold selfCast:    selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                    case Mushroom selfCast:     selfCast.rotation = Custom.DegToVec(data.angle + Custom.VecToDeg(s.rotation)); break;
                }
            }
            else
            {
                if (data.layer != PhysObjData.noLayer)
                {
                    self.ChangeCollisionLayer(data.layer);
                    self.collisionRange = data.range;
                    data.layer = PhysObjData.noLayer;
                    for (int i = 0; i < self.bodyChunks.Length; i++)
                    {
                        self.bodyChunks[i].goThroughFloors = false;
                        self.bodyChunks[i].collideWithObjects = true;
                        self.bodyChunks[i].collideWithSlopes = true;
                        self.bodyChunks[i].collideWithTerrain = true;
                    }
                }
                orig(self, eu);
            }

            if (IsKebabbable(self))
            {
                self.canBeHitByWeapons = true;
            }
        }

        private readonly Action<Action<PhysicalObject, bool>, PhysicalObject, bool> blockSetter = (orig, self, value) =>
        {
            orig(self, value && GetImpaled(self) == null);
        };

        private readonly Action<Action<PhysicalObject, bool>, PhysicalObject, bool> blockSetter2 = (orig, self, value) =>
        {
            orig(self, value || GetImpaled(self) != null);
        };

        private bool FixDuplicateStuckObjects(On.Weapon.orig_HitThisObject orig, Weapon self, PhysicalObject obj)
        {
            if (orig(self, obj))
            {
                foreach (var stick in self.abstractPhysicalObject.stuckObjects)
                {
                    if (stick is AbstractPhysicalObject.ImpaledOnSpearStick impaleStick && impaleStick.ObjectOnSpear == obj.abstractPhysicalObject)
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        private void Spear_HitSomethingWithoutStopping(On.Spear.orig_HitSomethingWithoutStopping orig, Spear self, PhysicalObject obj, BodyChunk chunk, PhysicalObject.Appendage appendage)
        {
            orig(self, obj, chunk, appendage);
            if (IsKebabbable(obj) && obj is not Fly)
            {
                TryImpale(self, obj, chunk.index);
            }
        }

        private void Player_PickupCandidate(ILContext il)
        {
            try
            {
                var cursor = new ILCursor(il);

                if (!cursor.TryGotoNext(MoveType.After, i => i.MatchStloc(4)))
                {
                    Logger.LogError("Missing instruction 1");
                    return;
                }

                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldloca_S, il.Body.Variables[4]);
                cursor.Emit(OpCodes.Ldloc_2);
                cursor.Emit(OpCodes.Ldloc_3);
                cursor.EmitDelegate<ModifyPickupPreference>(HookModifyPickupPreference);

                static void HookModifyPickupPreference(Player self, ref float effectiveDistance, int collisionLayer, int physicalObjectIndex)
                {
                    var physicalObject = self.room.physicalObjects[collisionLayer][physicalObjectIndex];

                    var impaledStick = GetImpaled(physicalObject);
                    if (impaledStick != null)
                    {
                        effectiveDistance += 500 * impaledStick.onSpearPosition;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }

        delegate void ModifyPickupPreference(Player self, ref float effectiveDistance, int collisionLayer, int physicalObjectIndex);

        private void Spear_HitSomething(ILContext il)
        {
            try
            {
                var cursor = new ILCursor(il);

                if (!cursor.TryGotoNext(i => i.MatchCallvirt<PhysicalObject.IHaveAppendages>("ApplyForceOnAppendage")))
                {
                    Logger.LogError("Missing instruction 1");
                    return;
                }

                if (!cursor.TryGotoNext(i => i.MatchLdarga(1)))
                {
                    Logger.LogError("Missing instruction 2");
                    return;
                }

                // Set original instruction to no-op to interrupt branch instructions
                cursor.Next.OpCode = OpCodes.Nop;
                cursor.Next.Operand = null;

                cursor.Index++;

                // Hook with an `if (HitSomething_TryImpale(this, result)) { return; }`
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.EmitDelegate<Func<Spear, SharedPhysics.CollisionResult, bool>>(HitSomething_TryImpale);
                cursor.Emit(OpCodes.Brtrue, il.Instrs[il.Instrs.Count - 2]);
                
                // Re-add original instruction
                cursor.Emit(OpCodes.Ldarga_S, il.Method.Parameters[1]);

                static bool HitSomething_TryImpale(Spear self, SharedPhysics.CollisionResult result)
                {
                    if (IsKebabbable(result.obj) && (result.obj is not Creature c || c.SpearStick(self, 0.55f, result.chunk, result.onAppendagePos, self.firstChunk.vel)))
                    {
                        self.room.PlaySound(SoundID.Spear_Stick_In_Creature, self.firstChunk);

                        TryImpale(self, result.obj, result.chunk.index);

                        if (self.abstractPhysicalObject.world.game.session is ArenaGameSession sess 
                            && sess.GameTypeSetup.spearHitScore != 0 && self.thrownBy is Player p && result.obj is Creature cr 
                            && !(cr.State is HealthState h && h.health <= 0f || cr.State is not HealthState hs && cr.State.dead))
                        {
                            sess.PlayerLandSpear(p, cr);
                        }

                        if (self.room.BeingViewed)
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                var randomOffset = Custom.DegToVec(360f * UnityEngine.Random.value) * self.firstChunk.vel.magnitude * UnityEngine.Random.value * 0.5f;
                                var vel = -self.firstChunk.vel * UnityEngine.Random.value * 0.5f + randomOffset;
                                self.room.AddObject(new WaterDrip(result.collisionPoint, vel, false));
                            }
                        }

                        return true;
                    }
                    return false;
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }
    }
}
