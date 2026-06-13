using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace RavenbreachMod
{
    // ragdoll velocity inheritance
    // stock behavior: rigidbodies start from zero velocity on animator->physics transition,
    // causing the "spring on death" snap. inject actor velocity at transition so physics
    // inherits momentum naturally.
    [HarmonyPatch(typeof(Actor), "OnStartRagdoll")]
    public static class RagdollVelocityInheritPatch
    {
        private static readonly FieldInfo _ragdollField =
            AccessTools.Field(typeof(Actor), "ragdoll");
        private static readonly FieldInfo _ragdollRigidbodiesField =
            AccessTools.Field(typeof(ActiveRaggy), "rigidbodies");

        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            try
            {
                if (__instance == null) return;
                Vector3 vel = __instance.Velocity();
                if (vel.magnitude < 0.5f) vel = Vector3.down * 0.5f;

                var ragdoll = _ragdollField?.GetValue(__instance) as ActiveRaggy;
                if (ragdoll == null) return;
                var rbs = _ragdollRigidbodiesField?.GetValue(ragdoll) as Rigidbody[];
                if (rbs == null || rbs.Length == 0) return;

                foreach (var rb in rbs)
                {
                    if (rb == null) continue;
                    float h = GetHeightFactor(__instance, rb.transform);
                    rb.velocity        = vel * h;
                    // Zero angular velocity first — the ragdoll joint springs
                    // generate angular momentum during the animator->physics transition
                    // which causes the "spider curl" death animation.
                    rb.angularVelocity = Vector3.zero;
                    float lat = new Vector2(vel.x, vel.z).magnitude;
                    if (lat > 0.5f)
                    {
                        Vector3 ax = Vector3.Cross(vel.normalized, Vector3.up).normalized;
                        rb.angularVelocity = ax * (lat * 0.08f); // reduced from 0.15f
                    }
                    // Also reduce joint spring forces to prevent snap-to-rest-pose
                    var joint = rb.GetComponent<CharacterJoint>();
                    if (joint != null)
                    {
                        var drive = new SoftJointLimit();
                        drive.limit = joint.lowTwistLimit.limit;
                        // Dampen spring to let physics settle naturally
                        rb.drag        = Mathf.Max(rb.drag, 0.5f);
                        rb.angularDrag = Mathf.Max(rb.angularDrag, 2.0f);
                    }
                }
            }
            catch { }
        }

        private static float GetHeightFactor(Actor actor, Transform bone)
        {
            if (bone == null) return 1f;
            float rel = bone.position.y - actor.Position().y;
            return Mathf.Lerp(0.8f, 1.1f, Mathf.Clamp01(rel / 1.8f));
        }
    }

    // keep first-person during ragdoll
    // vanilla FpsActorController.StartRagdoll calls ThirdPersonCamera() every time the
    // player ragdolls — including during airdrop reinforcement spawns, which looks terrible.
    //
    // spawn sequence that triggers this:
    //   AirdropAnimation.ActivateCamera -> SetOverrideCamera (aircraft view)
    //   Actor.SpawnAt -> CancelOverrideCamera + FirstPersonCamera (fp restored)
    //   Actor.KnockOver -> FallOver -> StartRagdoll -> ThirdPersonCamera (back to 3rd)
    //   Actor lands -> EndRagdoll -> fp restored
    //
    // fix: replace StartRagdoll entirely, keep all side effects, skip ThirdPersonCamera.
    // ragdoll physics still work — you just see them from first person.
    [HarmonyPatch(typeof(FpsActorController), "StartRagdoll")]
    public static class FpsStartRagdollNoCameraSwitch
    {
        private static readonly FieldInfo _aimInput   = AccessTools.Field(typeof(FpsActorController), "aimInput");
        private static readonly FieldInfo _crouchInput = AccessTools.Field(typeof(FpsActorController), "crouchInput");
        private static readonly FieldInfo _proneInput  = AccessTools.Field(typeof(FpsActorController), "proneInput");
        private static readonly FieldInfo _sprintInput = AccessTools.Field(typeof(FpsActorController), "sprintInput");
        private static readonly FieldInfo _controller  = AccessTools.Field(typeof(FpsActorController), "controller");

        private static readonly MethodInfo _resetMethod =
            AccessTools.Method(typeof(PlayerActionInput), "Reset");
        private static readonly MethodInfo _disableCC =
            AccessTools.Method(
                AccessTools.TypeByName("FirstPersonController"),
                "DisableCharacterController");
        private static readonly MethodInfo _setImposter =
            AccessTools.Method(typeof(Actor), "SetImposterRenderingEnabled",
                new[] { typeof(bool) });

        [HarmonyPrefix]
        public static bool Prefix(FpsActorController __instance)
        {
            try
            {
                var aim    = _aimInput?.GetValue(__instance);
                var crouch = _crouchInput?.GetValue(__instance);
                var prone  = _proneInput?.GetValue(__instance);
                var sprint = _sprintInput?.GetValue(__instance);
                if (aim    != null) _resetMethod?.Invoke(aim,    null);
                if (crouch != null) _resetMethod?.Invoke(crouch, null);
                if (prone  != null) _resetMethod?.Invoke(prone,  null);
                if (sprint != null) _resetMethod?.Invoke(sprint, null);

                var ctrl = _controller?.GetValue(__instance);
                if (ctrl != null) _disableCC?.Invoke(ctrl, null);

                if (__instance.actor != null)
                    _setImposter?.Invoke(__instance.actor, new object[] { false });

                // intentionally skip ThirdPersonCamera()
            }
            catch { }

            return false;
        }
    }
}
