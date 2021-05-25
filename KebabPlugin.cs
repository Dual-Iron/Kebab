using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Linq;

[assembly: ModLoaderInfo(Author = "Dual", ShortDescription = "Kebabs to your heart's content.", LongDescription =
    "Blue fruit and slime mold and grubs, oh my! This mod lets you spear most consumables, so you can make more interesting kebabs."
    )]

sealed class ModLoaderInfoAttribute : System.Attribute
{
    public string DisplayName { get; set; }
    public string ShortDescription { get; set; }
    public string LongDescription { get; set; }
    public string Author { get; set; }
}

namespace Kebab
{
    [BepInPlugin("com.github.dual.kebab", "Kebab", "1.0.0")]
    internal class KebabPlugin : BaseUnityPlugin
    {
        public void OnEnable()
        {
            On.Spear.Update += Spear_Update;
            On.Spear.HitSomethingWithoutStopping += Spear_HitSomethingWithoutStopping;
            On.Weapon.HitThisObject += FixDuplicateStuckObjects;
            On.PhysicalObject.Update += FixCollisionWithStuckObjects;
            IL.Spear.HitSomething += Spear_HitSomething;
        }

        private static bool IsKebabbable(PhysicalObject obj)
        {
            try
            {
                return obj is IPlayerEdible && obj.TotalMass < 0.4;
            }
            catch
            {
                return false;
            }
        }

        private static void Spear_Update(On.Spear.orig_Update orig, Spear self, bool eu)
        {
            // TODO: replace this with a proper FContainer for rendering stuck objects in front of the spear
            orig(self, eu);
            self.ChangeOverlap(!self.abstractPhysicalObject.stuckObjects.Any(a => a is AbstractPhysicalObject.ImpaledOnSpearStick i && i.ObjectOnSpear != null));
        }

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

        private static void TryImpale(Spear self, PhysicalObject obj, bool sound)
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

            if (sound)
                self.room.PlaySound(SoundID.Spear_Hit_Small_Creature, self.firstChunk);

            new AbstractPhysicalObject.ImpaledOnSpearStick(self.abstractPhysicalObject, obj.abstractPhysicalObject, 0, num2);
        }

        private static void FixCollisionWithStuckObjects(On.PhysicalObject.orig_Update orig, PhysicalObject self, bool eu)
        {
            orig(self, eu);
            if (IsKebabbable(self))
            {
                bool shouldCollide = self.grabbedBy.Count < 1 && !self.abstractPhysicalObject.stuckObjects.Any(a => a is AbstractPhysicalObject.ImpaledOnSpearStick);
                self.collisionRange = shouldCollide ? 50 : float.NegativeInfinity;
                self.firstChunk.collideWithObjects = shouldCollide;
                self.firstChunk.collideWithTerrain = shouldCollide;
                self.firstChunk.collideWithSlopes = shouldCollide;
            }
        }

        private static void Spear_HitSomethingWithoutStopping(On.Spear.orig_HitSomethingWithoutStopping orig, Spear self, PhysicalObject obj, BodyChunk chunk, PhysicalObject.Appendage appendage)
        {
            orig(self, obj, chunk, appendage);
            if (IsKebabbable(obj) && !(obj is Fly))
            {
                TryImpale(self, obj, false);
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
                    if (IsKebabbable(result.obj))
                    {
                        TryImpale(self, result.obj, true);
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
