using System;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis
{
    /// <summary>The stats a PoE copy is derived from. Rating is armor rating for armor, damage for weapons and ammo.</summary>
    public record ItemStats(string Name, float Rating, float Crit, float Speed, float Reach, float Value, float Weight);

    /// <summary>
    /// How each kind of PoE copy is derived from its source. The same functions build the copy records and recompute
    /// the copies from their originals' in-game stats (after SkyPatcher), so the two can't drift apart.
    /// </summary>
    public static class CopyStats
    {
        public static ItemStats Read(IMajorRecordGetter r) => r switch
        {
            IArmorGetter a => new(a.Name?.String ?? "", a.ArmorRating, 0, 0, 0, a.Value, a.Weight),
            IWeaponGetter w => new(w.Name?.String ?? "", w.BasicStats?.Damage ?? 0, w.Critical?.Damage ?? 0, w.Data?.Speed ?? 0, w.Data?.Reach ?? 0,
                w.BasicStats?.Value ?? 0, w.BasicStats?.Weight ?? 0),
            IAmmunitionGetter m => new(m.Name?.String ?? "", m.Damage, 0, 0, 0, m.Value, m.Weight),
            _ => throw new ArgumentException($"no stats for {r.GetType().Name}"),
        };

        public static void Write(IMajorRecord r, ItemStats s)
        {
            switch (r)
            {
                case Armor a:
                    a.Name = s.Name;
                    a.ArmorRating = s.Rating;
                    a.Value = (uint)Math.Max(0, s.Value);
                    a.Weight = s.Weight;
                    break;
                case Weapon w:
                    w.Name = s.Name;
                    if (w.BasicStats != null)
                    {
                        w.BasicStats.Damage = (ushort)Math.Max(0, s.Rating);
                        w.BasicStats.Value = (uint)Math.Max(0, s.Value);
                        w.BasicStats.Weight = s.Weight;
                    }
                    if (w.Critical != null) w.Critical.Damage = (ushort)Math.Max(0, s.Crit);
                    if (w.Data != null)
                    {
                        w.Data.Speed = s.Speed;
                        w.Data.Reach = s.Reach;
                    }
                    break;
                case Ammunition m:
                    m.Name = s.Name;
                    m.Damage = s.Rating;
                    m.Value = (uint)Math.Max(0, s.Value);
                    m.Weight = s.Weight;
                    break;
            }
        }

        public static Func<ItemStats, ItemStats> Identity => s => s;

        static float Trunc(float f) => MathF.Truncate(f);

        // ---------------- Masterwork (PoEMasterworkVariant.pas) ----------------

        public static Func<ItemStats, ItemStats> MasterworkArmor(int addPrice, int addArmor) => s => s with
        {
            Name = "Masterwork " + s.Name,
            Value = Trunc(s.Value * 1.35f + addPrice),
            Weight = s.Weight * 0.5f,
            Rating = s.Rating + addArmor,
        };

        public static Func<ItemStats, ItemStats> MasterworkWeapon(int addPrice, int addDamage) => s => s with
        {
            Name = "Masterwork " + s.Name,
            Value = Trunc(s.Value * 1.35f + addPrice),
            Weight = s.Weight * 0.5f,
            Rating = s.Rating + addDamage,
            Crit = s.Crit + 1,
        };

        // ---------------- clothing (PoEClothingPatcher.pas) ----------------

        public static Func<ItemStats, ItemStats> Quality(int add) => s => s with
        {
            Name = "Quality " + s.Name,
            Value = Trunc(s.Value * 1.35f + add),
        };

        // ---------------- crossbows (PoECrossbowVariant.pas) ----------------

        public static Func<ItemStats, ItemStats> Crossbow(string namePrefix, bool recurve, bool springLoaded, bool lightWeight, bool silenced) => s =>
        {
            s = s with { Name = namePrefix + s.Name };
            if (recurve) s = AddValue(s, 40);
            if (springLoaded)
            {
                if (s.Weight > 0) s = s with { Weight = Trunc(s.Weight + 1) };
                s = AddValue(s, 50) with { Speed = s.Speed * 0.85f };
            }
            if (lightWeight)
            {
                if (s.Weight > 0) s = s with { Weight = Trunc(s.Weight * 0.66f) };
                s = AddValue(s, 30) with { Speed = s.Speed * 1.15f };
            }
            if (silenced) s = AddValue(s, 40) with { Speed = s.Speed * 0.85f };
            return s;
        };

        static ItemStats AddValue(ItemStats s, int add) => s.Value > 0 ? s with { Value = s.Value + add } : s;

        // ---------------- longbows (PoELongbowVariant.pas) ----------------

        public static Func<ItemStats, ItemStats> Longbow(Func<string, string> rename) => s => s with
        {
            Name = rename(s.Name),
            Value = s.Value > 0 ? Trunc(s.Value * 1.3f + 100) : s.Value,
            Weight = s.Weight > 0 ? Trunc(s.Weight + 4) : s.Weight,
            Rating = s.Rating + 3,
            Speed = 0.2f + (s.Speed - 0.5f) / 0.5f * 0.18f,
            Crit = s.Crit + 1,
        };

        // ---------------- ammo (PoEAmmoPatcher.pas) ----------------

        public static Func<ItemStats, ItemStats> Lucky => s => s with
        {
            Name = "Lucky " + s.Name,
            Value = s.Value > 0 ? s.Value + 3 : s.Value,
        };

        public static Func<ItemStats, ItemStats> Renamed(string prefix) => s => s with { Name = prefix + s.Name };
    }
}
