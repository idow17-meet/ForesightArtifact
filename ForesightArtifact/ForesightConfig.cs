using BepInEx.Configuration;

namespace ForesightArtifact
{
    public class ForesightConfig
    {
        public ForesightConfig(ConfigFile config)
        {
            chestPriceCoefficient = config.Bind<float>(
                "Config",
                "chestPriceCoefficient",
                1.5f,
                "Coefficient for the chest \\ lunar's price increase, default is 1.5 (50% increase) rounded up (resulting in 2 for lunar)"
            );

            multiShopPriceCoefficient = config.Bind<float>(
                "Config",
                "multiShopPriceCoefficient",
                1.5f,
                "Coefficient for the multiShop's price increase, default is 1.5 (50% increase) being the same as chests for the sake of balance"
            );

            affectLunarPods = config.Bind<bool>(
                "Config",
                "affectLunarPods",
                true,
                "Whether the artifact should affect lunar pods, defaults to true"
            );

            showInPings = config.Bind<bool>(
                "Config",
                "showInPings",
                true,
                "Whether the artifact should also work when pinging a chest, defaults to true"
            );
        }

        public ConfigEntry<float> chestPriceCoefficient;
        public ConfigEntry<float> multiShopPriceCoefficient;
        public ConfigEntry<bool> affectLunarPods;
        public ConfigEntry<bool> showInPings;
    }
}
