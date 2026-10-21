using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace SnowCapMeltFix;

[HarmonyPatch]
public class SnowCapMeltFixModSystem : ModSystem
{
    private Harmony harmony;
    private static ICoreAPI vsApi;
    private static ModConfig mainConfig;
    private static ClientModConfig clientConfig;

    private const string patchCategoryServerOnly = "serveronly";
    private const string patchCategoryClientOnly = "clientonly";

    public override void Start(ICoreAPI api)
    {
        vsApi = api;
        var mainConfigFile = $"{Mod.Info.ModID}.json";

        try
        {
            mainConfig = api.LoadModConfig<ModConfig>(mainConfigFile);
        }
        catch
        {
            mainConfig = null;
        }

        mainConfig ??= new();
        api.StoreModConfig(mainConfig, mainConfigFile);
        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAllUncategorized();

        if (api.ModLoader.IsModEnabled("terrainslabs"))
        {
            // The exact same patch method works for the terrain slabs snow slab
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("TerrainSlabs.Source.Blocks.BlockSnowSlab"), "GetSnowCoveredVariant"), prefix: new(StopSnowBlockDeletion));
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        harmony.PatchCategory(patchCategoryServerOnly);
        api.Event.SaveGameLoaded += () =>
        {
            var data = api.WorldManager.SaveGame.GetData($"{Mod.Info.ModID}");

            if (data != null)
            {
                var worldData = SerializerUtil.Deserialize<WorldData>(data);
                api.Logger.Event($"Loaded {Mod.Info.ModID} data of version {worldData.dataVersion}, converting to world config");
                api.World.Config.SetBool("snowcapmeltfixScaleAltitudeTemperatureAdjustment", worldData.scaleAltitudeTemperatureAdjustment);
                api.World.Config.SetString("snowcapmeltfixAltitudeTemperatureAdjustmentRate", worldData.altitudeTemperatureAdjustmentRate.ToString());
                ((SaveGame)api.WorldManager.SaveGame).ModData.Remove(Mod.Info.ModID);
            }
        };
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        var clientConfigFile = $"{Mod.Info.ModID}-client.json";

        try
        {
            clientConfig = api.LoadModConfig<ClientModConfig>(clientConfigFile);
        }
        catch
        {
            clientConfig = null;
        }

        clientConfig ??= new();
        api.StoreModConfig(clientConfig, clientConfigFile);
        harmony.PatchCategory(patchCategoryClientOnly);
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockSnow), nameof(BlockSnow.GetSnowCoveredVariant))]
    public static bool StopSnowBlockDeletion(ref Block __result, BlockPos pos, float snowLevel)
    {
        __result = null;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WeatherSimulationRegion), "FromBytes")]
    public static void ClearSnowAccumSnapshotsOnResChange(WeatherSimulationRegion __instance, byte[] data)
    {
        // This is here to reset the stored snow accum snapshots if they are of the wrong size,
        // because previous versions of the mod allowed changing the height of snow accum snapshots
        var clear = false;
        var length = __instance.SnowAccumSnapshots.Length;
        var index = __instance.SnowAccumSnapshots.EndPosition;

        // foreach doesn't work on RingArray for some reason, so the classic method is used
        while (length-- > 0)
        {
            var snapshot = __instance.SnowAccumSnapshots[index];
            index = (index + 1) % __instance.SnowAccumSnapshots.Length;

            if (snapshot != null && snapshot.SnowAccumulationByRegionCorner.Data.Length != WeatherSimulationRegion.snowAccumResolution * WeatherSimulationRegion.snowAccumResolution * WeatherSimulationRegion.snowAccumResolution)
            {
                clear = true;
                break;
            }
        }

        if (clear)
        {
            // Just clearing the array should be fine, as the array is clear when starting a new world anyway
            __instance.SnowAccumSnapshots.Clear();
            vsApi.Logger.Event($"Malformed snow accum snapshot found in weather simulation region {__instance.regionX}/{__instance.regionZ}. Snow accum snapshots cleared for this region");
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperature))]
    public static IEnumerable<CodeInstruction> ChangeAltitudeTemperatureCurveGetScaledAdjustedTemperature(IEnumerable<CodeInstruction> instructions)
    {
        return ChangeAltitudeTemperatureCurve(instructions);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperatureFloat))]
    public static IEnumerable<CodeInstruction> ChangeAltitudeTemperatureCurveGetScaledAdjustedTemperatureFloat(IEnumerable<CodeInstruction> instructions)
    {
        return ChangeAltitudeTemperatureCurve(instructions);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Climate), nameof(Climate.GetScaledAdjustedTemperatureFloatClient))]
    public static IEnumerable<CodeInstruction> ChangeAltitudeTemperatureCurveGetScaledAdjustedTemperatureFloatClient(IEnumerable<CodeInstruction> instructions)
    {
        return ChangeAltitudeTemperatureCurve(instructions);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Climate), nameof(Climate.GetAdjustedTemperature))]
    public static IEnumerable<CodeInstruction> ChangeAltitudeTemperatureCurveGetAdjustedTemperature(IEnumerable<CodeInstruction> instructions)
    {
        return ChangeAltitudeTemperatureCurve(instructions);
    }

    public static IEnumerable<CodeInstruction> ChangeAltitudeTemperatureCurve(IEnumerable<CodeInstruction> instructions)
    {
        var injectionPerformed = false;

        foreach (var code in instructions)
        {
            if (!injectionPerformed && code.opcode == OpCodes.Div)
            {
                injectionPerformed = true;
                // Injects our custom altitude temperature modifier hook just before the altitude temperature modifier (1.5) is applied to distToSeaLevel via division
                // This hook consumes the loaded altitude temperature modifier and replaces it with its return value, which is then used by the division operation
                yield return new(OpCodes.Call, AccessTools.Method(typeof(SnowCapMeltFixModSystem), nameof(AdjustAltitudeTemperatureModifier)));
            }

            yield return code;
        }

        if (!injectionPerformed)
        {
            throw new InvalidOperationException("Failed to find injection point");
        }
    }

    public static float AdjustAltitudeTemperatureModifier(float original)
    {
        if (float.TryParse(vsApi.World.Config.GetString("snowcapmeltfixAltitudeTemperatureAdjustmentRate"), out var baseModifier))
        {
            baseModifier /= Climate.TemperatureScaleConversion;
        }
        else
        {
            baseModifier = original;
        }

        if (vsApi.World.Config.GetBool("snowcapmeltfixScaleAltitudeTemperatureAdjustment"))
        {
            const int defaultWorldAltitude = 256 - 110;
            var worldAltitude = vsApi.World.BlockAccessor.MapSizeY - vsApi.World.SeaLevel;
            return baseModifier * worldAltitude / defaultWorldAltitude;
        }

        return baseModifier;
    }

    [HarmonyPatchCategory(patchCategoryClientOnly)]
    public class ClientPatches
    {
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ShaderRegistry), "loadRegisteredShaderPrograms")]
        public static IEnumerable<CodeInstruction> InjectShaderPatcher(IEnumerable<CodeInstruction> instructions)
        {
            if (!clientConfig.PatchColormapShader)
                return instructions;

            List<CodeInstruction> codes = [.. instructions];

            for (int i = 0; i < codes.Count; ++i)
            {
                if (codes[i].Calls(AccessTools.Method(typeof(IAsset), nameof(IAsset.ToText))))
                {
                    codes.Insert(i + 1, new(OpCodes.Call, AccessTools.Method(typeof(ClientPatches), nameof(PatchColormapShader))));
                    codes.Insert(i - 1, new(OpCodes.Ldloc_2));
                    break;
                }
            }

            return codes.AsEnumerable();
        }

        public static string PatchColormapShader(IAsset asset, string text)
        {
            if (!clientConfig.PatchColormapShader || asset.Name != "colormap.vsh")
            {
                return text;
            }

            return text.Replace("uniform float seasonTemperature;", "uniform float seasonTemperature;\nuniform float altitudeTemperatureModifier;").Replace("seaLevelDist * 1.5", "seaLevelDist / altitudeTemperatureModifier");
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use))]
        public static IEnumerable<CodeInstruction> AddColormapUniform(IEnumerable<CodeInstruction> instructions)
        {
            if (!clientConfig.PatchColormapShader)
            {
                foreach (var code in instructions)
                {
                    yield return code;
                }

                yield break;
            }

            var inColormapBranch = false;
            var inSeasonTemperatureCall = false;
            var injectionPerformed = false;

            foreach (var code in instructions)
            {
                if (!injectionPerformed)
                {
                    if (inColormapBranch)
                    {
                        if (inSeasonTemperatureCall)
                        {
                            var uniformMethod = AccessTools.Method(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Uniform), [typeof(string), typeof(float)]);

                            if (code.Calls(uniformMethod))
                            {
                                injectionPerformed = true;
                                yield return code;
                                // Injects a Uniform call for the new altitudeTemperatureModifier uniform
                                yield return new(OpCodes.Ldarg_0);
                                yield return new(OpCodes.Ldstr, "altitudeTemperatureModifier");
                                yield return new(OpCodes.Ldc_R4, 1.5f);
                                yield return new(OpCodes.Call, AccessTools.Method(typeof(SnowCapMeltFixModSystem), nameof(AdjustAltitudeTemperatureModifier)));
                                yield return new(OpCodes.Call, uniformMethod);
                                continue;
                            }
                        }
                        else if (code.opcode == OpCodes.Ldstr && (string)code.operand == "seasonTemperature")
                        {
                            inSeasonTemperatureCall = true;
                        }
                    }
                    else if (code.opcode == OpCodes.Ldstr && (string)code.operand == "colormap.vsh")
                    {
                        inColormapBranch = true;
                    }
                }

                yield return code;
            }

            if (!injectionPerformed)
            {
                throw new InvalidOperationException("Failed to find injection point");
            }
        }
    }

    public class ModConfig
    {
        public bool ScaleAltitudeTemperatureAdjustment { get; set; } = false;
        public float AltitudeTemperatureAdjustmentRate { get; set; } = 6.375f;
    }

    public class ClientModConfig
    {
        public bool PatchColormapShader { get; set; } = true;
    }

    // Included for compatibility with v1.0.1 and earlier, which didn't use the world config system
    [ProtoContract]
    public class WorldData
    {
        public WorldData() { }

        public WorldData(int version, bool scaleTemperatureModifier, float temperatureModifier)
        {
            dataVersion = version;
            scaleAltitudeTemperatureAdjustment = scaleTemperatureModifier;
            altitudeTemperatureAdjustmentRate = temperatureModifier;
        }

        [ProtoMember(1)]
        public int dataVersion;
        [ProtoMember(2)]
        public bool scaleAltitudeTemperatureAdjustment;
        [ProtoMember(3)]
        public float altitudeTemperatureAdjustmentRate;
    }
}
