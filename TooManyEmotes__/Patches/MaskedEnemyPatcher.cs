﻿using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TooManyEmotes.Networking;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations.Rigging;
using static Unity.Collections.AllocatorManager;

namespace TooManyEmotes.Patches
{
    [HarmonyPatch]
    public class MaskedEnemyPatcher
    {
        public static int currentLevelSeed { get { return StartOfRound.Instance.randomMapSeed; } }
        public static AnimationClip defaultIdleClip;
        public static HashSet<PlayerControllerB> playersEmotedWithThisRound = new HashSet<PlayerControllerB>();


        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        public static void Init(StartOfRound __instance)
        {
            if (!NetworkManager.Singleton.IsServer)
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("TooManyEmotes-OnMaskedEnemyEmoteClientRpc", OnMaskedEnemyEmoteClientRpc);
        }


        [HarmonyPatch(typeof(StartOfRound), "Awake")]
        [HarmonyPostfix]
        public static void ResetValues(StartOfRound __instance)
        {
            if (!ConfigSync.instance.syncEnableMaskedEnemiesEmoting)
                return;
        }


        [HarmonyPatch(typeof(MaskedPlayerEnemy), "Start")]
        [HarmonyPostfix]
        public static void InitMaskedEnemy(MaskedPlayerEnemy __instance)
        {
            if (!ConfigSync.instance.syncEnableMaskedEnemiesEmoting)
                return;
            __instance.gameObject.AddComponent<EmoteControllerMaskedEnemy>();
        }


        [HarmonyPatch(typeof(RoundManager), "LoadNewLevel")]
        [HarmonyPrefix]
        public static void OnLoadNewLevel()
        {
            if (!ConfigSync.instance.syncEnableMaskedEnemiesEmoting)
                return;
            playersEmotedWithThisRound.Clear();
        }


        [HarmonyPatch(typeof(MaskedPlayerEnemy), "Update")]
        [HarmonyPostfix]
        public static void OnUpdate(MaskedPlayerEnemy __instance)
        {
            if (!ConfigSync.instance.syncEnableMaskedEnemiesEmoting || __instance.isEnemyDead || !EmoteControllerMaskedEnemy.allMaskedEnemyEmoteControllers.TryGetValue(__instance, out var emoteController))
                return;

            if (NetworkManager.Singleton.IsServer && emoteController.CanPerformEmote() && !emoteController.stoppedAndStaring)
            {
                emoteController.stoppedAndStaring = true;
                if (!CalculateShouldEmoteChance(emoteController))
                {
                    emoteController.emoteCount++;
                    return;
                }

                playersEmotedWithThisRound.Add(emoteController.lookingAtPlayer);
                var emote = GetRandomUnlockedEmote(emoteController);
                emoteController.pendingEmote = emote;

                float delay = GetRandomEmoteDelay(emoteController);
                float duration = GetRandomEmoteDuration(emoteController);
                Plugin.Log("Pre-performing emote on MaskedEnemy. Delay: " + delay + " ExtendedStopAndStareDuration: " + duration);
                emoteController.stopAndStareTimer = emoteController.stopAndStareTimer + duration;

                PerformEmoteAfterDelay(emote, emoteController, delay);
            }
        }


        public static bool CalculateShouldEmoteChance(EmoteControllerMaskedEnemy emoteController)
        {
            var random = new System.Random(currentLevelSeed + 1550 + 100 * emoteController.id + emoteController.emoteCount);
            float value = (float)random.NextDouble();
            bool shouldEmote = !playersEmotedWithThisRound.Contains(emoteController.lookingAtPlayer) || value <= ConfigSync.instance.syncMaskedEnemiesEmoteChanceOnEncounter;
            Plugin.Log("Calculating if masked enemy should emote: " + emoteController.maskedEnemy.name + ". Should emote: " + shouldEmote);
            return shouldEmote;
        }


        public static float GetRandomEmoteDelay(EmoteControllerMaskedEnemy emoteController)
        {
            var random = new System.Random(currentLevelSeed - 550 + 100 * emoteController.id + emoteController.emoteCount);
            Vector2 range = ConfigSync.syncMaskedEnemyEmoteRandomDelay;
            range = new Vector2(Mathf.Min(Mathf.Abs(range.x), Mathf.Abs(range.y)), Mathf.Max(Mathf.Abs(range.x), Mathf.Abs(range.y)));
            return (float)(random.NextDouble() * (range.y - range.x) + range.x);
        }


        public static float GetRandomEmoteDuration(EmoteControllerMaskedEnemy emoteController)
        {
            if (!ConfigSync.instance.syncOverrideStopAndStareDuration)
                return 0;
            var random = new System.Random(currentLevelSeed + 550 + 100 * emoteController.id + emoteController.emoteCount);
            Vector2 range = ConfigSync.syncMaskedEnemyEmoteRandomDuration;
            range = new Vector2(Mathf.Min(Mathf.Abs(range.x), Mathf.Abs(range.y)), Mathf.Max(Mathf.Abs(range.x), Mathf.Abs(range.y)));
            return (float)random.NextDouble() * (range.y - range.x) + range.x;
        }


        public static UnlockableEmote GetRandomUnlockedEmote(EmoteControllerMaskedEnemy emoteController)
        {
            var playerController = emoteController.maskedEnemy.mimickingPlayer;
            if (playerController == null)
                playerController = emoteController.lookingAtPlayer;
            if (playerController == null)
                return null;

            var emotesList = SessionManager.unlockedEmotes;
            if (!ConfigSync.instance.syncShareEverything && playerController != StartOfRound.Instance.localPlayerController)
                SessionManager.unlockedEmotesByPlayer.TryGetValue(playerController.playerUsername, out emotesList);
            if (emotesList == null)
                emotesList = SessionManager.unlockedEmotes;

            var random = new System.Random(currentLevelSeed + 100 * emoteController.id + emoteController.emoteCount);
            var emote = emotesList[random.Next(emotesList.Count)];

            if (emote.randomEmotePool != null && emote.randomEmotePool.Count > 0)
                emote = emote.randomEmotePool[random.Next(emote.randomEmotePool.Count)];
            return emote;
        }


        private static void SendUpdateMaskedEnemyEmoteToClients(EmoteControllerMaskedEnemy emoteController, int emoteId)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(emoteController.maskedEnemy.NetworkObjectId);
            writer.WriteValueSafe(emoteId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll("TooManyEmotes-OnMaskedEnemyEmoteClientRpc", writer);
        }


        private static void OnMaskedEnemyEmoteClientRpc(ulong clientId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                return;

            ulong maskedEnemyNetworkId;
            int emoteId;
            reader.ReadValue(out maskedEnemyNetworkId);
            reader.ReadValue(out emoteId);

            Plugin.Log("Receiving update for masked enemy emote from server. Masked enemy id: " + maskedEnemyNetworkId + " EmoteId: " + emoteId);
            foreach (var emoteController in EmoteControllerMaskedEnemy.allMaskedEnemyEmoteControllers.Values)
            {
                if (emoteController.maskedEnemy.NetworkObjectId == maskedEnemyNetworkId)
                {
                    emoteController.PerformEmote(EmotesManager.allUnlockableEmotes[emoteId]);
                    return;
                }
            }
            Plugin.LogError("Failed to find masked enemy with id: " + maskedEnemyNetworkId);
        }

        
        public static void PerformEmoteAfterDelay(UnlockableEmote emote, EmoteControllerMaskedEnemy emoteController, float delay)
        {
            IEnumerator PerformEmote()
            {
                yield return new WaitForSeconds(delay);
                float distanceToTarget = Vector3.Distance(emoteController.lookingAtPlayer.transform.position, emoteController.maskedEnemy.transform.position);
                if (distanceToTarget > 20f)
                    Plugin.LogWarning("Failed to perform emote on masked enemy. Target player is too far away. Distance: " + distanceToTarget);
                else if (!emoteController.CanPerformEmote())
                    Plugin.LogWarning("Failed to perform emote on masked enemy: " + emoteController.maskedEnemy.name);
                else
                {
                    emoteController.PerformEmote(emote);
                    if (NetworkManager.Singleton.IsServer)
                        SendUpdateMaskedEnemyEmoteToClients(emoteController, emote.emoteId);
                }
            }
            if (emote != null)
                emoteController.maskedEnemy.StartCoroutine(PerformEmote());
        }
    }
}
