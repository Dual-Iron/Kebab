using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RWCustom;
using StaticTables;
using System;

[assembly: ModLoaderInfo(Author = "Dual", ShortDescription = "Kebabs to your heart's content.", LongDescription =
    "Blue fruit and slime mold and grubs, oh my! This mod lets you spear most consumables, so you can make more interesting kebabs."
    )]

sealed class ModLoaderInfoAttribute : Attribute
{
    public string DisplayName { get; set; }
    public string ShortDescription { get; set; }
    public string LongDescription { get; set; }
    public string Author { get; set; }
}

namespace Kebab
{
    struct PhysObjData : IWeakData<PhysicalObject>, IConstructible<PhysicalObject>
    {
        public int layer;

        void IConstructible<PhysicalObject>.Construct(PhysicalObject owner, object state)
        {
            layer = -1;
        }
    }

    struct SpearData : IWeakData<Spear>
    {
        public FContainer container;
    }

    [BepInPlugin("com.github.dual.kebab", "Kebab", "1.0.0")]
    internal class KebabPlugin : BaseUnityPlugin
    {
        private static AbstractPhysicalObject.ImpaledOnSpearStick GetImpaled(PhysicalObject self)
        {
            if (self.grabbedBy.Count > 1)
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
                return obj is IPlayerEdible e && e.Edible && obj.TotalMass < 0.4f;
            }
            catch
            {
                return false;
            }
        }

        public void OnEnable()
        {
            On.AbstractPhysicalObject.AbstractObjectStick.Deactivate += AbstractObjectStick_Deactivate;
            On.Weapon.AddToContainer += Weapon_AddToContainer;
            On.Room.AddObject += Room_AddObject;
            On.PhysicalObject.Update += PhysicalObject_Update;
            On.Spear.HitSomethingWithoutStopping += Spear_HitSomethingWithoutStopping;
            On.Weapon.HitThisObject += FixDuplicateStuckObjects;
            IL.Spear.HitSomething += Spear_HitSomething;

            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithTerrain"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithSlopes"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_CollideWithObjects"), blockSetter).Apply();
            new Hook(typeof(PhysicalObject).GetMethod("set_GoThroughFloors"), blockSetter2).Apply();
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

        private static void TryImpale(Spear self, PhysicalObject obj, int chunk)
        {
            int num = 0;
            int num2 = 0;

            for (int i = 0; i < self.abstractPhysicalObject.stuckObjects.Count; i++)
                if (self.abstractPhysicalObject.stuckObjects[i] is AbstractPhysicalObject.ImpaledOnSpearStick o)
                {
                    if (o.ObjectOnSpear == obj.abstractPhysicalObject)
                    {
                        return;
                    }
                    if (o.onSpearPosition == num2)
                    {
                        num2++;
                    }
                    num++;
                }

            if (num > 5 || num2 >= 5)
                return;

            if (self.Data().Get<SpearData>().container is FContainer f)
            {
                var drawable = obj as IDrawable ?? obj.graphicsModule;
                if (drawable != null)
                    foreach (var camera in self.room.game.cameras)
                    {
                        camera.MoveObjectToContainer(drawable, f);
                    }
            }

            new AbstractPhysicalObject.ImpaledOnSpearStick(self.abstractPhysicalObject, obj.abstractPhysicalObject, chunk, num2);
        }

        private void Weapon_AddToContainer(On.Weapon.orig_AddToContainer orig, Weapon self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContatiner)
        {
            if (self is Spear s)
            {
                ref var data = ref s.Data().Get<SpearData>();

                data.container ??= new();

                orig(self, sLeaser, rCam, data.container);

                data.container.RemoveFromContainer();

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
            // TODO figure out how to fix vulture grubs in general
            // TODO make sure this doesn't break anything
            ref var data = ref self.Data().Get<PhysObjData>();
            var impaled = GetImpaled(self);
            if (impaled != null && impaled.A.Room.index == impaled.B.Room.index && impaled.A.realizedObject is Spear s && impaled.B.realizedObject == self && !s.slatedForDeletetion)
            {
                for (int i = 0; i < self.bodyChunks.Length; i++)
                {
                    self.bodyChunks[i].goThroughFloors = true;
                    self.bodyChunks[i].collideWithObjects = false;
                    self.bodyChunks[i].collideWithSlopes = false;
                    self.bodyChunks[i].collideWithTerrain = false;
                }

                if (data.layer == -1)
                    data.layer = self.collisionLayer;

                self.collisionLayer = 2;

                orig(self, eu);

                self.collisionLayer = 2;

                self.firstChunk.MoveFromOutsideMyUpdate(eu, s.firstChunk.pos + s.rotation * Custom.LerpMap(impaled.onSpearPosition, 0f, 4f, 15f, -15f));
            }
            else
            {
                if (data.layer != -1)
                {
                    self.collisionLayer = data.layer;
                    data.layer = -1;
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
                for (int i = 0; i < self.abstractPhysicalObject.stuckObjects.Count; i++)
                {
                    if (self.abstractPhysicalObject.stuckObjects[i].A == obj.abstractPhysicalObject ||
                        self.abstractPhysicalObject.stuckObjects[i].B == obj.abstractPhysicalObject)
                        return false;
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
