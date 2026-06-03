using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SnowCapMeltFix;

[HarmonyPatch]
public class SnowCapMeltFixModSystem : ModSystem
{
    private Harmony harmony;
    private static ICoreServerAPI sapi;
    private static ModConfig config;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        var configFile = $"{Mod.Info.ModID}.json";

        try
        {
            config = sapi.LoadModConfig<ModConfig>(configFile);
        }
        catch
        {
            config = null;
        }

        config ??= new();
        sapi.StoreModConfig(config, configFile);
        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
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
                break;
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

        if (__instance.SnowAccumSnapshots[lastElementAdded]?.SnowAccumulationByRegionCorner?.Height != config.SnowAccumVerticalResolution)
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
        return config.SnowAccumVerticalResolution;
    }

    public static int SnapshotYToBlockY(int snapshotY, WeatherSystemBase ws)
    {
        return (int)GameMath.Lerp(ws.api.World.SeaLevel, ws.api.World.BlockAccessor.MapSizeY - 1, (float)snapshotY / (config.SnowAccumVerticalResolution - 1));
    }

    public class ModConfig
    {
        public int SnowAccumVerticalResolution { get; set; } = 2;
    }
}
