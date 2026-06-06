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
    private static WorldData worldData;
    private static string worldDataSyncChannel;

    private const int saveDataVersion = 0;
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
        worldDataSyncChannel = $"{Mod.Info.ModID}_worlddatasync";
        api.Network.RegisterChannel(worldDataSyncChannel).RegisterMessageType<WorldData>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        harmony.PatchCategory(patchCategoryServerOnly);
        api.Event.SaveGameCreated += () =>
        {
            worldData = new(saveDataVersion, mainConfig.ScaleAltitudeTemperatureAdjustment, mainConfig.AltitudeTemperatureAdjustmentRate / Climate.TemperatureScaleConversion);
            api.WorldManager.SaveGame.StoreData($"{Mod.Info.ModID}", SerializerUtil.Serialize(worldData));
            api.Logger.Event($"Saved {Mod.Info.ModID} data for new save game");
        };
        api.Event.SaveGameLoaded += () =>
        {
            var data = api.WorldManager.SaveGame.GetData($"{Mod.Info.ModID}");

            if (data != null)
            {
                worldData = SerializerUtil.Deserialize<WorldData>(data);
                api.Logger.Event($"Loaded {Mod.Info.ModID} data of version {worldData.dataVersion}");
            }
            else
            {
                worldData = null;
                api.Logger.Notification($"Loaded save game does not have {Mod.Info.ModID} data");
            }
        };
        api.Event.PlayerNowPlaying += p =>
        {
            if (worldData != null)
            {
                api.Network.GetChannel(worldDataSyncChannel).SendPacket(worldData, p);
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
        api.Network.GetChannel(worldDataSyncChannel).SetMessageHandler<WorldData>(m =>
        {
            // No point overwriting this in singleplayer
            worldData ??= m;
        });
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
        worldData = null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockSnow), nameof(BlockSnow.GetSnowCoveredVariant))]
    public static bool StopSnowBlockDeletion(ref Block __result, BlockPos pos, float snowLevel)
    {
        __result = null;
        return false;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(WeatherSimulationRegion), nameof(WeatherSimulationRegion.UpdateSnowAccumulation))]
    public static IEnumerable<CodeInstruction> SimRegionChangeSnowAccumVerticalResolution(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = [.. instructions];
        int occurrences = 0;
        var yFound = false;

        for (int i = 0; i < codes.Count; ++i)
        {
            if (occurrences < 5 && codes[i].LoadsField(AccessTools.Field(typeof(WeatherSimulationRegion), "snowAccumResolution")))
            {
                ++occurrences;

                if (occurrences == 2 || occurrences == 5)
                {
                    codes[i] = new(OpCodes.Call, AccessTools.Method(typeof(SnowCapMeltFixModSystem), nameof(GetSnowAccumVerticalResolution)));
                }
            }
            else if (!yFound && codes[i].opcode == OpCodes.Stloc_S && ((LocalBuilder)codes[i].operand).LocalIndex == 7)
            {
                for (int j = i; j >= 0; --j)
                {
                    if (codes[j].opcode == OpCodes.Ldloc_S && ((LocalBuilder)codes[i].operand).LocalIndex == 5)
                    {
                        yFound = true;
                        // Keep the first instruction in the block, as it is the target of a jump and provides one of the arguments for the injected method call anyway
                        ++j;
                        codes.RemoveRange(j, i - j);
                        List<CodeInstruction> replacement =
                        [
                                new(OpCodes.Ldarg_0),
                                new(OpCodes.Ldfld, AccessTools.Field(typeof(WeatherSimulationRegion), "ws")),
                                new(OpCodes.Call, AccessTools.Method(typeof(SnowCapMeltFixModSystem), nameof(SnapshotYToBlockY)))
                        ];
                        codes.InsertRange(j, replacement);
                        i = j + replacement.Count;
                        break;
                    }
                }
            }

            if (occurrences >= 5 && yFound)
            {
                break;
            }
        }

        return codes.AsEnumerable();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WeatherSimulationRegion), "FromBytes")]
    public static void ClearSnowAccumSnapshotsOnResChange(WeatherSimulationRegion __instance, byte[] data)
    {
        var lastElementAdded = __instance.SnowAccumSnapshots.EndPosition - 1;

        if (lastElementAdded < 0)
        {
            lastElementAdded += __instance.SnowAccumSnapshots.Length;
        }

        if (__instance.SnowAccumSnapshots[lastElementAdded]?.SnowAccumulationByRegionCorner?.Height != mainConfig.SnowAccumVerticalResolution)
        {
            // Just clearing the array should be fine, as the array is clear when starting a new world anyway
            __instance.SnowAccumSnapshots.Clear();
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(WeatherSimulationSnowAccum), "GetSnowUpdate")]
    public static IEnumerable<CodeInstruction> SimSnowAccumChangeSnowAccumVerticalResolution(IEnumerable<CodeInstruction> instructions)
    {
        int occurrences = 0;

        foreach (var code in instructions)
        {
            if (occurrences < 2 && code.opcode == OpCodes.Ldloc_2)
            {
                ++occurrences;

                if (occurrences == 2)
                {
                    yield return new(OpCodes.Call, AccessTools.Method(typeof(SnowCapMeltFixModSystem), nameof(GetSnowAccumVerticalResolution)));
                    continue;
                }
            }

            yield return code;
        }
    }

    public static int GetSnowAccumVerticalResolution()
    {
        return mainConfig.SnowAccumVerticalResolution;
    }

    public static int SnapshotYToBlockY(int snapshotY, WeatherSystemBase ws)
    {
        return (int)GameMath.Lerp(ws.api.World.SeaLevel, ws.api.World.BlockAccessor.MapSizeY - 1, (float)snapshotY / (mainConfig.SnowAccumVerticalResolution - 1));
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
        return worldData == null ? original : GetTransformedAltitudeTemperatureModifier();
    }

    public static float GetTransformedAltitudeTemperatureModifier()
    {
        var baseModifier = worldData.altitudeTemperatureAdjustmentRate;

        if (worldData.scaleAltitudeTemperatureAdjustment)
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
        public int SnowAccumVerticalResolution { get; set; } = 2;
        public bool ScaleAltitudeTemperatureAdjustment { get; set; } = false;
        public float AltitudeTemperatureAdjustmentRate { get; set; } = 6.375f;
    }

    public class ClientModConfig
    {
        public bool PatchColormapShader { get; set; } = true;
    }

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
