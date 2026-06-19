using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mirage;
using Cysharp.Threading.Tasks;
using NuclearOption.Networking;
using Mirage.Serialization;

namespace CooldownMunitions;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class CooldownMunitions : BaseUnityPlugin
{
    public static CooldownMunitions Instance { get; private set; }
    public static ConfigEntry<float> CooldownModifier;
    private Harmony harmony;

    public void Awake()
    {
        CooldownModifier = Config.Bind("Base",      // The section under which the option is shown
                                         "Cooldown Modifier",  // The key of the configuration option in the configuration file
                                         1f, // The default value
                                         "A global modifier applied to cooldowns this mod generates."); // Description of the option to show in the config file
        Instance = this;
        this.harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        //harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Unit"), "UserCode_CmdAskFullAmmoInternal_-1739804082"), prefix: new HarmonyMethod(typeof(RpcSyncAmmoTotalPrefix), nameof(RpcSyncAmmoTotalPrefix.Prefix)));
        this.harmony.PatchAll();
        Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} is loaded and patches applied.");
    }
    
    private void OnDestroy()
    {
        Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} is being destroyed");
    }
    
    public void Log(string log)
    {
        Logger.LogInfo(log);
    }
}


public static class MountedMissileCooldowns
{
    private class CooldownEntry
    {
        public float Remaining;
        public float OriginalTime;
        public float OriginalDuration;
        public WeaponStation WeaponStation;
    }

    private static readonly Dictionary<MountedMissile, CooldownEntry> _cooldowns = new Dictionary<MountedMissile, CooldownEntry>();

    public static void SetCooldown(MountedMissile missile, float seconds, WeaponStation weaponStation)
    {
        _cooldowns[missile] = new CooldownEntry
        {
            Remaining = seconds * (float)CooldownMunitions.CooldownModifier.BoxedValue,
            OriginalTime = Time.timeSinceLevelLoad,
            OriginalDuration = seconds,
            WeaponStation = weaponStation
        };
    }

    public static bool TryGetCooldown(MountedMissile missile, out float remaining, out float originalSeconds)
    {
        remaining = 0f;
        originalSeconds = 0f;

        if (!_cooldowns.TryGetValue(missile, out var entry))
            return false;
            
        remaining = entry.OriginalDuration - (Time.timeSinceLevelLoad - entry.OriginalTime);
        originalSeconds = entry.OriginalDuration;

        return true;
    }
    
    public static void CheckCooldown(MountedMissile missile)
    {
        if (!_cooldowns.TryGetValue(missile, out var entry))
            return;
        
        if (entry.OriginalDuration - (Time.timeSinceLevelLoad - entry.OriginalTime) <= 0) {
            if (missile.attachedUnit.IsLocalPlayer) {
                Player p = missile.attachedUnit.GetPlayer();
                if (p.Allocation > missile.info.costPerRound) {
                    p.AddAllocation(-1*missile.info.costPerRound);
                    DoCooldown(missile, entry.WeaponStation);
                }
            } else {
                DoCooldown(missile, entry.WeaponStation);
                missile.attachedUnit.NetworkHQ.AddFunds(-1 * missile.info.costPerRound);
            }
        }
    }
    
    public static void DoCooldown(MountedMissile missile, WeaponStation station)
    {
        CooldownMunitions.Instance?.Log($"Rearming a missile.");
        missile.Rearm();
        station.AccountAmmo();
        station.Updated();
        missile.ReportReloading(true);
        _cooldowns.Remove(missile);
    }

    public static void TrackCooldownAsync(MountedMissile missile)
    {
        CooldownMunitions.Instance?.Log($"Setting up Async call.");
        if (!_cooldowns.TryGetValue(missile, out var entry))
            return;

        float remaining = entry.OriginalDuration - (Time.timeSinceLevelLoad - entry.OriginalTime);

        if (remaining <= 0f)
        {
            CheckCooldown(missile);
            return;
        }

        UniTask.Void(async () =>
        {
            await UniTask.Delay(TimeSpan.FromSeconds(remaining), DelayType.DeltaTime);

            if (!_cooldowns.TryGetValue(missile, out var currentEntry) || currentEntry.OriginalTime != entry.OriginalTime)
                return;

            CheckCooldown(missile);
        });
    }
}

[HarmonyPatch(typeof(MountedMissile), nameof(MountedMissile.Fire))]
public static class MountedMissile_Fire_LeifCooldownMunitions
{
    public static void Postfix(MountedMissile __instance, WeaponStation ___weaponStation, Unit ___attachedUnit)
    {
        float price = __instance.info.costPerRound * 1000;
        CooldownMunitions.Instance?.Log($"Munition fired with price: {price}");
        
        float cd = (float)Math.Pow(price, 0.72) + 40;
        MountedMissileCooldowns.SetCooldown(__instance, cd, ___weaponStation);
        MountedMissileCooldowns.TrackCooldownAsync(__instance);
        __instance.ReportReloading(true);
    }
}


[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.GetReloadStatusMin))]
public static class WeaponStation_GetReloadStatusMin_LeifCooldownMunitions
{
    public static bool Prefix(WeaponStation __instance, List<Weapon> ___Weapons, ref float __result, ref int ___weaponIndex)
    {
        if(!__instance.Reloading) {
            __result = 0f;
        } else {
            float num = 1f;
            foreach(Weapon weapon in ___Weapons) {
                if (weapon is MountedMissile missile)
                {
                    if (MountedMissileCooldowns.TryGetCooldown(missile, out var remaining, out var originalSeconds)) {
                        num = Mathf.Min(num, remaining / originalSeconds);
                    } else {
                        num = 0f;
                    }
                } else {
                    num = Mathf.Min(num, weapon.GetReloadProgress());
                }
            }
            __result = num;
        }
        return false;
    } 
}

[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.GetReloadStatusMax))]
public static class WeaponStation_GetReloadStatusMax_LeifCooldownMunitions
{
    public static bool Prefix(ref WeaponStation __instance, List<Weapon> ___Weapons, ref float __result)
    {
        if(!__instance.Reloading) {
            __result = 0f;
        } else {
            float num = 1f;
            foreach(Weapon weapon in ___Weapons) {
                if (weapon is MountedMissile missile)
                {
                    if (MountedMissileCooldowns.TryGetCooldown(missile, out var remaining, out var originalSeconds)) {
                        num = Mathf.Min(num, remaining / originalSeconds);
                    }                    
                } else {
                    num = Mathf.Min(num, weapon.GetReloadProgress());
                }
            }
            __result = num;
        }
        return false;
    }
}

//[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.Ready))]
public static class WeaponStation_Ready_LeifCooldownMunitions
{
    private static bool Prefix(ref WeaponStation __instance, ref bool __result) {
        if (!__instance.WeaponInfo.missile)
            return true;
            
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.LaunchMount))]
public static class WeaponStation_LaunchMount_LeifCooldownMunitions
{
    public static bool Prefix(ref WeaponStation __instance, List<Weapon> ___Weapons, ref int ___weaponIndex)
    {   
        if (___weaponIndex >= ___Weapons.Count)
        {
            CooldownMunitions.Instance?.Log($"LaunchMount resetting weaponIndex");
            ___weaponIndex = 0;
        }

        return true;
    }
}

public class PluginInfo
{
    public const string PLUGIN_GUID = "com.leifo.cooldownmunitions";
    public const string PLUGIN_NAME = "CooldownMunitions";
    public const string PLUGIN_VERSION = "1.0.3";
}
