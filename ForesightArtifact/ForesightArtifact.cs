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
        "0.0.1")]

    [R2APISubmoduleDependency(nameof(LanguageAPI), nameof(PrefabAPI))]


    public class ForesightArtifact : BaseUnityPlugin
    {
        AssetBundle bundle;
        ArtifactDef foresightArtifactDef;

        internal static GameObject chestSyncPrefab;

        float pickupNameXPos = 0.5f;
        float pickNameYPos = 0.75f;
        float syncInterval = 0.5f;
        float priceCoefficient = 1.5f;

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
            InitArtifact();
            CreateChestSynchronizer();

            Run.onRunStartGlobal += (obj) =>
            {
                if (RunArtifactManager.instance.IsArtifactEnabled(foresightArtifactDef.artifactIndex))
                {
                    On.RoR2.ChestBehavior.PickFromList += SaveAndSyncChestPickup;
                    On.RoR2.Hologram.HologramProjector.BuildHologram += AddPickupNameToHologram;
                    On.RoR2.PurchaseInteraction.GetDisplayName += AddPickupNameToDisplay;
                    On.RoR2.Run.GetDifficultyScaledCost_int += RaiseChestPrices;
                }
            };
            Run.onRunDestroyGlobal += (obj) =>
            {
                On.RoR2.ChestBehavior.PickFromList -= SaveAndSyncChestPickup;
                On.RoR2.Hologram.HologramProjector.BuildHologram -= AddPickupNameToHologram;
                On.RoR2.PurchaseInteraction.GetDisplayName -= AddPickupNameToDisplay;
                On.RoR2.Run.GetDifficultyScaledCost_int -= RaiseChestPrices;
            };
            ArtifactCatalog.getAdditionalEntries += (list) =>
            {
                list.Add(foresightArtifactDef);
            };
#if DEBUG
            On.RoR2.Networking.GameNetworkManager.OnClientConnect += (self, user, t) => { };
#endif
        }

        private int RaiseChestPrices(On.RoR2.Run.orig_GetDifficultyScaledCost_int orig, Run self, int baseCost)
        {
            var origPrice = orig(self, baseCost);
            return (int)(origPrice * priceCoefficient);
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

                StartCoroutine(SyncChestPickup(self.netId, dropPickup, syncInterval));
            }
        }

        private void AddPickupNameToHologram(On.RoR2.Hologram.HologramProjector.orig_BuildHologram orig, HologramProjector self)
        {
            orig(self);

            var chestBehav = self.GetComponent<ChestBehavior>();
            if (!chestBehav) return;

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

        private IEnumerator SyncChestPickup(NetworkInstanceId chestId, PickupIndex pickup, float delay)
        {
            while (!NetworkUser.AllParticipatingNetworkUsersReady())
            {
                yield return new WaitForSeconds(delay);
            }
            NetworkChestSync.instance.RpcAddPickup(chestId, pickup);
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
    public void RpcAddPickup(NetworkInstanceId chestId, PickupIndex pickupIndex)
    {
        chestPickups.Add(chestId, PickupCatalog.GetPickupDef(pickupIndex));
#if DEBUG
        Debug.Log($"Synced pickup in chest id [{chestId.ToString()}]");
#endif
    }

    public bool TryGetPickup(NetworkInstanceId chestId, out PickupDef pickup)
    {
        return this.chestPickups.TryGetValue(chestId, out pickup);
    }
}
