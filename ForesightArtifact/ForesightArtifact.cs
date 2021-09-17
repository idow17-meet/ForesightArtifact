using BepInEx;
using R2API;
using R2API.Utils;
using RoR2;
using RoR2.Hologram;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace ForesightArtifact
{
    [BepInDependency("com.bepis.r2api")]

    [BepInPlugin(
        "com.SpacePotato.ForesightArtifact",
        "ForesightArtifact",
        "0.2.0")]

    [R2APISubmoduleDependency(nameof(LanguageAPI), nameof(PrefabAPI), nameof(ArtifactAPI))]


    public class ForesightArtifact : BaseUnityPlugin
    {
        ForesightConfig config;
        AssetBundle bundle;
        ArtifactDef foresightArtifactDef;

        internal static GameObject chestSyncPrefab;

        float pickupNameXPos = 0f;
        float pickNameYPos = 0.75f;
        float syncInterval = 0.5f;

        void CreateChestSynchronizer()
        {
            var chestSync = new GameObject("chestSync");
            chestSync.AddComponent<NetworkIdentity>();
            chestSyncPrefab = chestSync.InstantiateClone("chestSynchronizer", true, "SpacePotato.ForesightArtifact.ForesightArtifact.CreateChestSynchronizer");
            Destroy(chestSync);
            chestSyncPrefab.AddComponent<NetworkChestSync>();
        }

        void InitArtifact()
        {
#if DEBUG
            Logger.LogMessage("GetManifestResourceNames: " + string.Join(" and ", Assembly.GetExecutingAssembly().GetManifestResourceNames()));
#endif
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ForesightArtifact.AssetBundle.foresight"))
            {
                bundle = AssetBundle.LoadFromStream(stream);
            }

            foresightArtifactDef = bundle.LoadAsset<ArtifactDef>("Assets/ForesightArtifact/Foresight.asset");
        }

        public void Awake()
        {
            config = new ForesightConfig(this.Config);
            InitArtifact();
            CreateChestSynchronizer();

            Run.onRunStartGlobal += (obj) =>
            {
                if (RunArtifactManager.instance.IsArtifactEnabled(foresightArtifactDef.artifactIndex))
                {
                    On.RoR2.ChestBehavior.PickFromList += SaveAndSyncChestPickup;
                    On.RoR2.Hologram.HologramProjector.BuildHologram += AddPickupNameToHologram;
                    if (config.showInPings.Value) On.RoR2.PurchaseInteraction.GetDisplayName += AddPickupNameToDisplay;
                    On.RoR2.PurchaseInteraction.Awake += RaiseChestPrices;
                    On.RoR2.MultiShopController.Start += RaiseMultishopPrices;
                }
            };
            Run.onRunDestroyGlobal += (obj) =>
            {
                On.RoR2.ChestBehavior.PickFromList -= SaveAndSyncChestPickup;
                On.RoR2.Hologram.HologramProjector.BuildHologram -= AddPickupNameToHologram;
                On.RoR2.PurchaseInteraction.GetDisplayName -= AddPickupNameToDisplay;
                On.RoR2.PurchaseInteraction.Awake -= RaiseChestPrices;
                On.RoR2.MultiShopController.Start -= RaiseMultishopPrices;
            };
            if (!ArtifactAPI.Add(foresightArtifactDef))
            {
                Logger.LogError("Failed to add foresight artifact!");
            }
#if DEBUG
            On.RoR2.Networking.GameNetworkManager.OnClientConnect += (self, user, t) => { };
#endif
        }

        private void RaiseChestPrices(On.RoR2.PurchaseInteraction.orig_Awake orig, PurchaseInteraction self)
        {
            orig(self);
            var chest = self.GetComponent<ChestBehavior>();
            if (!chest || (IsLunarPod(chest) && !config.affectLunarPods.Value)) return;

            self.cost = (int)Mathf.Ceil(self.cost * config.chestPriceCoefficient.Value);
        }

        private void RaiseMultishopPrices(On.RoR2.MultiShopController.orig_Start orig, MultiShopController self)
        {
            orig(self);
            self.Networkcost = (int)(self.Networkcost * config.multiShopPriceCoefficient.Value);
            if (self.GetFieldValue<GameObject[]>("terminalGameObjects") is GameObject[] terminalGameObjects)
            {
                GameObject[] array = terminalGameObjects;
                for (int i = 0; i < array.Length; i++)
                {
                    PurchaseInteraction component = array[i].GetComponent<PurchaseInteraction>();
                    component.Networkcost = self.Networkcost;
                    component.costType = self.costType;
                }
            }
        }

        private string GetStylizedPickupName(PickupDef pickup)
        {
            var (nameToken, colorIndex) = pickup switch
            {
                PickupDef _ when EquipmentCatalog.GetEquipmentDef(pickup.equipmentIndex) is EquipmentDef def => (def.nameToken, def.colorIndex),
                PickupDef _ when ItemCatalog.GetItemDef(pickup.itemIndex) is ItemDef def => (def.nameToken, def.colorIndex),
                _ => ("", ColorCatalog.ColorIndex.None),
            };

            var color = ColorCatalog.GetColorHexString(colorIndex);
            return $"<color=#{color}>{Language.GetString(nameToken)}</color>";
        }

        private void SaveAndSyncChestPickup(On.RoR2.ChestBehavior.orig_PickFromList orig, ChestBehavior self, List<PickupIndex> dropList)
        {
            orig(self, dropList);

            var dropPickup = self.GetFieldValue<PickupIndex>("dropPickup");
            if (dropPickup == null || dropPickup == PickupIndex.none)
            {
                Debug.LogWarning($"Failed to get pickupIndex of Chest {self.netId}");
                return;
            }

            if (NetworkServer.active)
            {
                if (!NetworkChestSync.instance)
                {
                    var instance = Instantiate(chestSyncPrefab);
                    NetworkServer.Spawn(instance);
                }

                StartCoroutine(SyncChestPickup(self.netId, PickupCatalog.GetPickupDef(dropPickup).internalName, syncInterval));
            }
        }

        private void AddPickupNameToHologram(On.RoR2.Hologram.HologramProjector.orig_BuildHologram orig, HologramProjector self)
        {
            orig(self);

            var chestBehav = self.GetComponent<ChestBehavior>();
            if (!chestBehav || (IsLunarPod(chestBehav) && !config.affectLunarPods.Value)) return;

            var netId = chestBehav.netId;
            PickupDef pickup = null;

            if (!NetworkChestSync.instance.TryGetPickup(netId, out pickup))
            {
                Debug.LogWarning($"Failed getting pickup for chest {netId}");
                return;
            }

            var contentObj = self.GetFieldValue<GameObject>("hologramContentInstance");

            if (!contentObj)
            {
                Debug.LogWarning($"No content instance for chest {netId}'s hologram projector");
                return;
            }

            var txtCopy = Instantiate(contentObj.transform.GetChild(2).gameObject);
            txtCopy.GetComponent<TextMeshPro>().text = GetStylizedPickupName(pickup);
            txtCopy.transform.SetParent(contentObj.transform, false);
            txtCopy.GetComponent<RectTransform>().anchoredPosition = new Vector2(pickupNameXPos, pickNameYPos);
        }

        private string AddPickupNameToDisplay(On.RoR2.PurchaseInteraction.orig_GetDisplayName orig, PurchaseInteraction self)
        {
            var displayName = orig(self);

            var chest = self.GetComponent<ChestBehavior>();
            if (!chest)
            {
                Debug.LogWarning("Failed to get Chest Behaviour of Purchase Interaction: " + self.gameObject.name);
                return displayName;
            }

            if (NetworkChestSync.instance.TryGetPickup(chest.netId, out PickupDef pickup))
            {
                return $"{displayName} ({GetStylizedPickupName(pickup)})";
            }

            Debug.LogWarning("Failed to get pickup for chest " + chest.netId);
            return displayName;
        }

        private IEnumerator SyncChestPickup(NetworkInstanceId chestId, string pickupName, float delay)
        {
            while (!NetworkUser.AllParticipatingNetworkUsersReady())
            {
                yield return new WaitForSeconds(delay);
            }
            NetworkChestSync.instance.RpcAddPickup(chestId, pickupName);
        }

        private bool IsLunarPod(ChestBehavior chest)
        {
            return chest.name.StartsWith("LunarChest") && chest.lunarChance == 1f;
        }
    }
}

internal class NetworkChestSync : NetworkBehaviour
{
    private Dictionary<NetworkInstanceId, PickupDef> chestPickups = new Dictionary<NetworkInstanceId, PickupDef>();
    public static NetworkChestSync instance;

    public void Awake()
    {
        instance = this;
        Debug.Log("Chest synchronizer initialized");
    }

    [ClientRpc]
    public void RpcAddPickup(NetworkInstanceId chestId, string pickupName)
    {
        chestPickups.Add(chestId, PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(pickupName)));
#if DEBUG
        Debug.Log($"Synced pickup in chest id [{chestId.ToString()}]");
#endif
    }

    public bool TryGetPickup(NetworkInstanceId chestId, out PickupDef pickup)
    {
        return this.chestPickups.TryGetValue(chestId, out pickup);
    }
}
