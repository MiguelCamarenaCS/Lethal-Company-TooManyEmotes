﻿using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TooManyEmotes.Patches;
using UnityEngine.Rendering;

namespace TooManyEmotes.Compatibility
{
    [HarmonyPatch]
    public static class MirrorDecorPatcher
    {
        public static bool Enabled = false;

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPrefix]
        public static void ApplyPatch()
        {
            if (Plugin.IsModLoaded("quackandcheese.mirrordecor"))
            {
                Enabled = true;
                ThirdPersonEmoteController.localPlayerBodyLayer = 23;
                ThirdPersonEmoteController.defaultShadowCastingMode = ShadowCastingMode.On;
                Plugin.Log("Applied patch for MirrorDecor");
            }
        }
    }
}
