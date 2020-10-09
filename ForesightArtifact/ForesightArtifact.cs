using BepInEx;
using R2API;
using R2API.Utils;
using RoR2;
using RoR2.Hologram;
using System.Collections;
using System.Collections.Generic;
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
        internal static GameObject chestSyncPrefab;

        float itemNameXPos = 0.5f;
        float itemNameYPos = 0.75f;
        float syncInterval = 0.5f;

        void CreateChestSynchronizer()
        {
            var chestSync = new GameObject("chestSync");
            chestSync.AddComponent<NetworkIdentity>();
            chestSyncPrefab = chestSync.InstantiateClone("chestSynchronizer", true, "SpacePotato.ForesightArtifact.ForesightArtifact.CreateChestSynchronizer");
            Destroy(chestSync);
            chestSyncPrefab.AddComponent<NetworkChestSync>();
        }

        public void Awake()
        {
            On.RoR2.ChestBehavior.PickFromList += SaveAndSyncChestItem;
            On.RoR2.Hologram.HologramProjector.BuildHologram += AddItemNameToHologram;
            On.RoR2.PurchaseInteraction.GetDisplayName += AddItemNameToDisplay;

            CreateChestSynchronizer();
#if DEBUG
            On.RoR2.Networking.GameNetworkManager.OnClientConnect += (self, user, t) => { };
#endif
        }

        public void OnDestroy()
        {
            On.RoR2.ChestBehavior.PickFromList -= SaveAndSyncChestItem;
            On.RoR2.Hologram.HologramProjector.BuildHologram -= AddItemNameToHologram;
            On.RoR2.PurchaseInteraction.GetDisplayName -= AddItemNameToDisplay;
        }

        private ItemIndex GetChestItemIndex(GameObject chest)
        {
            ItemIndex item = ItemIndex.None;

            var behaviour = chest.GetComponent<ChestBehavior>();
            if (behaviour)
            {
                var dropPickup = behaviour.GetFieldValue<PickupIndex>("dropPickup");
                if (dropPickup != PickupIndex.none)
                {
                    var pickupDef = RoR2.PickupCatalog.GetPickupDef(dropPickup);
                    item = pickupDef.itemIndex;
                }
            }

            return item;
        }

        private string GetStylizedItemName(ItemDef item)
        {
            var color = ColorCatalog.GetColorHexString(item.colorIndex);
            return $"<color=#{color}>{Language.GetString(item.nameToken)}</color>";
        }

        private void SaveAndSyncChestItem(On.RoR2.ChestBehavior.orig_PickFromList orig, ChestBehavior self, List<PickupIndex> dropList)
        {
            orig(self, dropList);

            var dropPickup = self.GetFieldValue<PickupIndex>("dropPickup");
            if (dropPickup == null || dropPickup == PickupIndex.none)
            {
                Debug.LogWarning($"Failed to get pickupIndex of Chest {self.netId}");
                return;
            }

            var pickupDef = RoR2.PickupCatalog.GetPickupDef(dropPickup);
            var item = pickupDef.itemIndex;

            if (NetworkServer.active)
            {
                if (!NetworkChestSync.instance)
                {
                    var instance = Instantiate(chestSyncPrefab);
                    NetworkServer.Spawn(instance);
                }

                if (item != ItemIndex.None)
                {
                    StartCoroutine(SyncChestItem(self.netId, item, syncInterval));
                }
            }
        }

        private void AddItemNameToHologram(On.RoR2.Hologram.HologramProjector.orig_BuildHologram orig, HologramProjector self)
        {
            orig(self);

            var chestBehav = self.GetComponent<ChestBehavior>();
            if (!chestBehav) return;

            var netId = chestBehav.netId;
            ItemDef item = null;
            
            if (!NetworkChestSync.instance.TryGetItem(netId, out item))
            {
                Debug.LogWarning($"Failed getting item for chest {netId}");
                return;
            }

            var contentObj = self.GetFieldValue<GameObject>("hologramContentInstance");

            if (!contentObj)
            {
                Debug.LogWarning($"No content instance for chest {netId}'s hologram projector");
                return;
            }

            var txtCopy = Instantiate(contentObj.transform.GetChild(2).gameObject);
            txtCopy.GetComponent<TextMeshPro>().text = GetStylizedItemName(item);
            txtCopy.transform.SetParent(contentObj.transform, false);
            txtCopy.GetComponent<RectTransform>().anchoredPosition = new Vector2(itemNameXPos, itemNameYPos);
        }

        private string AddItemNameToDisplay(On.RoR2.PurchaseInteraction.orig_GetDisplayName orig, PurchaseInteraction self)
        {
            var displayName = orig(self);

            var chest = self.GetComponent<ChestBehavior>();
            if (!chest)
            {
                Debug.LogWarning("Failed to get Chest Behaviour of Purchase Interaction: " + self.gameObject.name);
                return displayName;
            }

            if (NetworkChestSync.instance.TryGetItem(chest.netId, out ItemDef item))
            {
                return $"{displayName} ({GetStylizedItemName(item)})";
            }

            Debug.LogWarning("Failed to get item for chest " + chest.netId);
            return displayName;
        }

        private IEnumerator SyncChestItem(NetworkInstanceId chestId, ItemIndex item, float delay)
        {
            while (!NetworkUser.AllParticipatingNetworkUsersReady())
            {
                yield return new WaitForSeconds(delay);
            }
            NetworkChestSync.instance.RpcAddItem(chestId, item);
        }
    }
}

internal class NetworkChestSync : NetworkBehaviour
{
    private Dictionary<NetworkInstanceId, ItemDef> chestItems = new Dictionary<NetworkInstanceId, ItemDef>();
    public static NetworkChestSync instance;

    public void Awake()
    {
        instance = this;
        Debug.Log("Chest synchronizer initialized");
    }

    [ClientRpc]
    public void RpcAddItem(NetworkInstanceId chestId, ItemIndex itemId)
    {
        chestItems.Add(chestId, ItemCatalog.GetItemDef(itemId));
#if DEBUG
        Debug.Log($"Synced item in chest id [{chestId.ToString()}]");
#endif
    }

    public bool TryGetItem(NetworkInstanceId chestId, out ItemDef item) 
    {
        return this.chestItems.TryGetValue(chestId, out item);
    }
}
