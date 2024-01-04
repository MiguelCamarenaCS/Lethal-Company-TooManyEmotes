﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using HarmonyLib;
using GameNetcodeStuff;
using System.IO;
using BepInEx;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;
using UnityEditor;
using System.Security.Cryptography;
using TooManyEmotes.Config;
using TooManyEmotes.Networking;
using DunGen;
using Unity.Netcode;

namespace TooManyEmotes.Patches
{

    [HarmonyPatch]
    public static class TerminalPatcher
    {
        public static Terminal terminalInstance;
        public static List<UnlockableEmote> emoteSelection;
        public static List<UnlockableEmote> mysteryEmoteSelection;
        public static int currentEmoteCredits;
        static string confirmEmoteOpeningText = "You have requested to order a new emote.";

        public static UnlockableEmote purchasingEmote;
        public static bool initializedTerminalNodes = false;

        public static int emoteStoreSeed = 0;

        [HarmonyPatch(typeof(Terminal), "Awake")]
        [HarmonyPostfix]
        public static void InitializeTerminal(Terminal __instance) {
            terminalInstance = __instance;
            emoteSelection = new List<UnlockableEmote>();
            mysteryEmoteSelection = new List<UnlockableEmote>();
            if (!initializedTerminalNodes)
                EditExistingTerminalNodes();
        }


        public static void EditExistingTerminalNodes() {
            initializedTerminalNodes = true;
            foreach (TerminalNode node in terminalInstance.terminalNodes.specialNodes)
            {
                if (node.name == "Start")
                {
                    string keyword = "Type \"Help\" for a list of commands.";
                    int insertIndex = node.displayText.IndexOf(keyword);
                    if (insertIndex != -1)
                    {
                        insertIndex += keyword.Length;
                        string addText = "\n\n[TooManyEmotes]\nType \"Emotes\" for a list of commands.";
                        node.displayText = node.displayText.Insert(insertIndex, addText);
                    }
                    else
                        Debug.LogError("Failed to add emotes tip to terminal. Maybe an update broke it?");
                }

                else if (node.name == "HelpCommands")
                {
                    string keyword = "[numberOfItemsOnRoute]";
                    int insertIndex = node.displayText.IndexOf(keyword);
                    if (insertIndex != -1)
                    {
                        string addText = ">EMOTES\n" +
                            "For a list of Emote commands.\n\n";
                        node.displayText = node.displayText.Insert(insertIndex, addText);
                    }
                }
            }
        }


        [HarmonyPatch(typeof(Terminal), "TextPostProcess")]
        [HarmonyPrefix]
        public static void TextPostProcess(ref string modifiedDisplayText, TerminalNode node) {
            if (modifiedDisplayText.Length <= 0)
                return;

            if (modifiedDisplayText.Contains("[emoteUnlockablesSelectionList]"))
            {
                string replacementText = "Remaining emote credit balance: $" + currentEmoteCredits + ".\n";
                if (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything)
                    replacementText += "Remaining group credit balance: $" + terminalInstance.groupCredits + ".\n";
                replacementText += "\n";

                int longestNameSize = 0;
                foreach (var emote in emoteSelection)
                    longestNameSize = Mathf.Max(longestNameSize, emote.displayName.Length);
                foreach (var emote in emoteSelection)
                {
                    string priceText = StartOfRoundPatcher.unlockedEmotes.Contains(emote) ? "[Purchased]" : "$" + emote.price;
                    replacementText += string.Format("* {0}{1}   //   {2}\n", emote.displayNameColorCoded, new string(' ', longestNameSize - emote.displayName.Length), priceText);
                }
                modifiedDisplayText = modifiedDisplayText.Replace("[emoteUnlockablesSelectionList]", replacementText);
            }
        }


        [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
        [HarmonyPrefix]
        public static bool ParsePlayerSentence(ref TerminalNode __result, Terminal __instance) {

            if (__instance.screenText.text.Length == 0)
                return true;

            string screenText = __instance.screenText.text;
            string[] lines = screenText.Split('\n');
            string input = lines.Last().ToLower().Trim(' ');
            string[] args = input.Split(' ');
            UnlockableEmote emote = null;

            if (args.Length == 0)
                return true;

            if (!ConfigSync.isSynced)
            {
                if (input.StartsWith("emote"))
                {
                    __result = BuildTerminalNodeHostDoesNotHaveMod();
                    return false;
                }
                else
                    return true;
            }


            if (__instance.screenText.text.Contains(confirmEmoteOpeningText) && !__instance.screenText.text.Contains("Canceled order."))
            {
                if ("confirm".StartsWith(input))
                {
                    if (StartOfRoundPatcher.unlockedEmotes.Contains(purchasingEmote))
                    {
                        Debug.Log("Attempted to confirm purchase on emote that was already unlocked. Emote: " + purchasingEmote.displayName);
                        __result = BuildTerminalNodeAlreadyUnlocked(purchasingEmote);
                    }
                    else if (currentEmoteCredits + (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything ? terminalInstance.groupCredits : 0) < purchasingEmote.price)
                    {
                        Debug.Log("Attempted to confirm purchase with insufficient emote credits. Current credits: " + currentEmoteCredits + ". " + (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything ? ("Group credits: " + terminalInstance.groupCredits + ". ") : "") + "Emote price: " + purchasingEmote.price);
                        __result = BuildTerminalNodeInsufficientFunds(purchasingEmote);
                    }
                    else
                    {
                        //StartOfRoundPatcher.UnlockEmoteLocal(purchasingEmote);

                        int oldEmoteCredits = currentEmoteCredits;
                        int oldGroupCredits = terminalInstance.groupCredits;

                        int dEmoteCredits = -Mathf.Min(currentEmoteCredits, purchasingEmote.price);
                        int dGroupCredits = ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything ? -Mathf.Min(terminalInstance.groupCredits, purchasingEmote.price + dEmoteCredits) : 0;

                        currentEmoteCredits += dEmoteCredits;
                        terminalInstance.groupCredits += dGroupCredits;

                        EmoteSyncManager.SendOnUnlockEmoteUpdate(purchasingEmote.emoteId);
                        if (dGroupCredits > 0)
                            terminalInstance.SyncGroupCreditsServerRpc(terminalInstance.groupCredits, terminalInstance.numberOfItemsInDropship);
                        Debug.Log("Purchasing emote: " + purchasingEmote.displayName + ". Price: " + purchasingEmote.price);
                        __result = BuildTerminalNodeOnPurchased(purchasingEmote, dEmoteCredits != 0 ? currentEmoteCredits : -1, dGroupCredits != 0 ? terminalInstance.groupCredits : -1);
                    }
                }
                else
                {
                    Plugin.Log("Canceling emote order.");
                    __result = BuildCustomTerminalNode("Canceled order.\n\n");
                }
                purchasingEmote = null;
                return false;
            }

            if (input.StartsWith("emotes"))
                input = input.Replace("emotes", "emote");




            if (input.StartsWith("emote cheat ") && GameNetworkManager.Instance.localPlayerController.playerUsername == "Flip" && GameNetworkManager.Instance.localPlayerController.IsServer)
            {
                input = input.Replace("emote cheat ", "");
                if (input.StartsWith("rotate"))
                {
                    EmoteSyncManager.RotateEmoteSelectionServerRpc();
                    __result = BuildCustomTerminalNode("Rotated emotes.\n------------------------------\n[emoteUnlockablesSelectionList]\n\n", clearPreviousText: true);
                }

                else if (input.StartsWith("resetship"))
                {
                    StartOfRoundPatcher.ResetProgressLocal();
                    __result = BuildCustomTerminalNode("Reset ship emotes.\n\n", clearPreviousText: true);
                }

                else if (input.StartsWith("morecredits"))
                {
                    currentEmoteCredits = 10000;
                    __result = BuildCustomTerminalNode("New emote credit balance: " + currentEmoteCredits + "\n\n", clearPreviousText: true);
                }

                return false;
            }


            if (input.StartsWith("emote"))
            {
                if (input == "emote")
                {
                    __result = BuildTerminalNodeHome();
                    return false;
                }
                input = input.Substring(6);
                emote = TryGetEmoteCurrentSelection(input);
            }
            else
            {
                emote = TryGetEmoteCurrentSelection(input, reliable: true);
                if (emote == null)
                    return true;
            }


            if (emote != null)
            {
                if (StartOfRoundPatcher.unlockedEmotes.Contains(emote))
                {
                    Plugin.Log("Attempted to start purchase on emote that was already unlocked. Emote: " + emote.displayName);
                    __result = BuildTerminalNodeAlreadyUnlocked(emote);
                }
                else if (currentEmoteCredits + (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything ? terminalInstance.groupCredits : 0) < emote.price)
                {
                    Plugin.Log("Attempted to start purchase with insufficient emote credits. Current credits: " + currentEmoteCredits + ". " + (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything ? ("Group credits: " + terminalInstance.groupCredits + ". ") : "") + "Emote price: " + emote.price);
                    __result = BuildTerminalNodeInsufficientFunds(emote);
                }
                else
                {
                    Plugin.Log("Started purchasing emote: " + emote.emoteName);
                    purchasingEmote = emote;
                    __result = BuildTerminalNodeConfirmDenyPurchase(emote);
                }
                return false;
            }
            else
            {
                Plugin.Log("Attempted to start purchase on invalid emote, or emote was not in current rotation. Input emote: " + input);
                __result = BuildTerminalNodeInvalidEmote();
                return false;
            }
        }


        [HarmonyPatch(typeof(DepositItemsDesk), "SellItemsClientRpc")]
        [HarmonyPostfix]
        public static void GainEmoteCredits(int itemProfit, int newGroupCredits, int itemsSold, float buyingRate, DepositItemsDesk __instance)
        {
            if (((int)Traverse.Create(__instance).Field("__rpc_exec_stage").GetValue()) == 2 && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost))
            {
                int emoteCreditsProfit = (int)(itemProfit * ConfigSync.instance.syncAddEmoteCreditsMultiplier);
                Plugin.Log("Gained " + itemProfit + " group credits.");
                Plugin.Log("Gained " + emoteCreditsProfit + " emote credits. GainEmoteCreditsMultiplier: " + ConfigSync.instance.syncAddEmoteCreditsMultiplier);
                currentEmoteCredits += emoteCreditsProfit;
            }
        }


        [HarmonyPatch(typeof(TimeOfDay), "SetNewProfitQuota")]
        [HarmonyPostfix]
        public static void RotateEmoteSelectionPerQuota()
        {
            if (NetworkManager.Singleton.IsServer)
                EmoteSyncManager.RotateEmoteSelectionServerRpc();
        }


        public static void RotateNewEmoteSelection()
        {
            Plugin.Log("Rotating emote selection in store. Seed: " + emoteStoreSeed);

            System.Random random = new System.Random(emoteStoreSeed + (!ConfigSync.instance.syncShareEverything ? (int)StartOfRound.Instance.localPlayerController.playerClientId : 0));
            emoteSelection.Clear();

            for (int i = 0; i < ConfigSync.instance.syncNumEmotesStoreRotation; i++)
            {
                UnlockableEmote emote = null;
                if (ConfigSync.instance.syncDisableRaritySystem)
                    emote = GetRandomEmoteNotUnlocked(StartOfRoundPatcher.allUnlockableEmotes, random);
                else
                {
                    double itemRarity = random.NextDouble();
                    Plugin.Log("Rotate new emote value: " + itemRarity);
                    float threshold = 1 - ConfigSync.instance.syncRotationChanceEmoteTier3;
                    if (itemRarity >= threshold)
                        emote = GetRandomEmoteNotUnlocked(StartOfRoundPatcher.allEmotesTier3, random);
                    if (emote == null)
                    {
                        threshold -= ConfigSync.instance.syncRotationChanceEmoteTier2;
                        if (itemRarity >= threshold)
                            emote = GetRandomEmoteNotUnlocked(StartOfRoundPatcher.allEmotesTier2, random);
                    }
                    if (emote == null)
                    {
                        threshold -= ConfigSync.instance.syncRotationChanceEmoteTier1;
                        if (itemRarity >= threshold)
                            emote = GetRandomEmoteNotUnlocked(StartOfRoundPatcher.allEmotesTier1, random);
                    }
                    if (emote == null)
                        emote = GetRandomEmoteNotUnlocked(StartOfRoundPatcher.allEmotesTier0, random);
                }

                if (emote != null)
                    emoteSelection.Add(emote);
            }
            emoteSelection.Sort((item1, item2) => item1.rarity.CompareTo(item2.rarity));
        }


        public static UnlockableEmote GetRandomEmoteNotUnlocked(List<UnlockableEmote> emoteList, System.Random random)
        {
            var notUnlocked = new List<UnlockableEmote>();
            foreach (var emote in emoteList)
            {
                if (!StartOfRoundPatcher.unlockedEmotes.Contains(emote) && !emoteSelection.Contains(emote) && emote.purchasable)
                    notUnlocked.Add(emote);
            }
            if (notUnlocked.Count > 0)
            {
                int emoteIndex = random.Next(notUnlocked.Count);
                return notUnlocked[emoteIndex];
            }
            return null;
        }


        public static TerminalNode BuildTerminalNodeHome() {

            TerminalNode homeTerminalNode = new TerminalNode {
                displayText = "[TooManyEmotes]\n\n" +
                    "Store\n" +
                    "------------------------------\n" +
                    "[emoteUnlockablesSelectionList]\n\n",
                clearPreviousText = true,
                acceptAnything = false
            };
            
            return homeTerminalNode;
        }


        static TerminalNode BuildTerminalNodeConfirmDenyPurchase(UnlockableEmote emote) {
            TerminalNode terminalNode = new TerminalNode {
                displayText = confirmEmoteOpeningText + "\n" +
                "> [" + emote.displayNameColorCoded + "]\n\n",
                isConfirmationNode = true,
                acceptAnything = false,
                clearPreviousText = true
            };

            terminalNode.displayText += "Emote credit balance: $" + currentEmoteCredits + "\n";
            if (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything)
                terminalNode.displayText += "Group credit balance: $" + terminalInstance.groupCredits + "\n";
            terminalNode.displayText += "\n";
            terminalNode.displayText += "Please CONFIRM or DENY.\n\n";

            return terminalNode;
        }


        static TerminalNode BuildTerminalNodeOnPurchased(UnlockableEmote emote, int newEmoteCredits, int newGroupCredits) {
            TerminalNode terminalNode = new TerminalNode {
                displayText = "You have successfully purchased a new emote!\n" +
                "> [" + emote.displayNameColorCoded + "]\n\n",
                buyUnlockable = true,
                clearPreviousText = true,
                acceptAnything = false,
                playSyncedClip = 0
            };

            if (newEmoteCredits != -1)
                terminalNode.displayText += "New emote credit balance: $" + newEmoteCredits + "\n";
            if (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything && newGroupCredits != -1)
                terminalNode.displayText += "New group credit balance: $" + newGroupCredits + "\n";
            terminalNode.displayText += "\n";

            int page = Mathf.Max((StartOfRoundPatcher.unlockedEmotes.Count - 1) / 8) + 1;
            int slot = StartOfRoundPatcher.unlockedEmotes.Count % 8;
            terminalNode.displayText += "Your new emote is registered in your emote menu.\n" +
                "Page: " + page + "\n" +
                "Slot: " + slot + ".\n\n";

            return terminalNode;
        }


        static TerminalNode BuildTerminalNodeAlreadyUnlocked(UnlockableEmote emote) {
            TerminalNode terminalNode = new TerminalNode {
                displayText = "You have already purchased this emote!\n" +
                "> [" + emote.displayNameColorCoded + "]\n\n",
                clearPreviousText = false,
                acceptAnything = false
            };

            return terminalNode;
        }


        static TerminalNode BuildTerminalNodeInsufficientFunds(UnlockableEmote emote) {
            TerminalNode terminalNode = new TerminalNode {
                displayText = "You could not afford this emote!\n" +
                "> [" + emote.displayNameColorCoded + "]\n\n" +
                "Emote credit balance is $" + currentEmoteCredits + "\n",
                clearPreviousText = true,
                acceptAnything = false
            };

            if (ConfigSync.instance.syncPurchaseEmotesWithDefaultCurrency && ConfigSync.instance.syncShareEverything)
                terminalNode.displayText += "Group credit balance is $" + currentEmoteCredits + "\n";

            terminalNode.displayText += "Cost of emote is $" + emote.price + "\n\n";
            return terminalNode;
        }


        static TerminalNode BuildTerminalNodeInvalidEmote(string emoteName = "") {
            TerminalNode terminalNode = new TerminalNode {
                displayText = "Emote does not exist, or is not available in the current rotation.",
                clearPreviousText = false,
                acceptAnything = false
            };
            if (emoteName != "")
                terminalNode.displayText += ("\n\"" + emoteName + "\"");
            terminalNode.displayText += "\n";
            return terminalNode;
        }


        static TerminalNode BuildTerminalNodeHostDoesNotHaveMod(string emoteName = "")
        {
            TerminalNode terminalNode = new TerminalNode
            {
                displayText = "You cannot use the emote commands menu until you are synced with the host.\n\n" +
                "You may also be seeing this because the host does not have this mod.\n" +
                "If this is the case, you will already have access to every emote in your emote wheel. Enjoy!\n\n",
                clearPreviousText = true,
                acceptAnything = false
            };
            if (emoteName != "")
                terminalNode.displayText += ("\n\"" + emoteName + "\"");
            terminalNode.displayText += "\n";
            return terminalNode;
        }


        static TerminalNode BuildCustomTerminalNode(string displayText, bool clearPreviousText = false, bool acceptAnything = false, bool isConfirmationNode = false)
        {
            TerminalNode terminalNode = new TerminalNode
            {
                displayText = displayText,
                clearPreviousText = clearPreviousText,
                acceptAnything = false,
                isConfirmationNode = isConfirmationNode
            };
            return terminalNode;
        }


        static UnlockableEmote TryGetEmote(string emoteNameInput, IEnumerable<UnlockableEmote> emoteList = null, bool reliable = false) {
            if (emoteList == null)
                emoteList = StartOfRoundPatcher.allUnlockableEmotes;
            UnlockableEmote getEmote = null;
            foreach (var emote in emoteList)
            {
                string emoteName = emote.displayName.ToLower();
                if (reliable)
                {
                    if ((emoteNameInput.Length >= 4 && emoteName.StartsWith(emoteNameInput)) || emoteNameInput == emoteName || emoteNameInput == emoteName.Split(' ')[0] || emoteNameInput.Split(' ')[0] == emoteName.Split(' ')[0])
                    {
                        if (getEmote == null || emoteName.Length < getEmote.displayName.Length)
                            getEmote = emote;
                    }
                }
                else if (emoteName.StartsWith(emoteNameInput))
                {
                    if (getEmote == null || emoteName.Length < getEmote.displayName.Length)
                        getEmote = emote;
                }
            }
            return getEmote;
        }
        static UnlockableEmote TryGetEmoteCurrentSelection(string emoteNameInput, bool reliable = false) => TryGetEmote(emoteNameInput, emoteSelection, reliable);
        static UnlockableEmote TryGetEmoteUnlockedEmotes(string emoteNameInput, bool reliable = false) => TryGetEmote(emoteNameInput, StartOfRoundPatcher.unlockedEmotes, reliable);
    }
}