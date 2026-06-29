using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2External;

public class Config
{
    public bool EspEnabled { get; set; } = true;
    public bool BoxEnabled { get; set; } = true;
    public int BoxType { get; set; } = 0; // 0=2D, 1=corner, 2=2D filled
    public bool HealthBarEnabled { get; set; } = true;
    public bool SkeletonEnabled { get; set; } = false;
    public bool NameEnabled { get; set; } = true;
    public bool WeaponEnabled { get; set; } = true;
    public bool MoneyEnabled { get; set; } = false;
    public bool DistanceEnabled { get; set; } = true;
    public bool TeamCheck { get; set; } = true;

    public int EnemyColor { get; set; } = Color.FromArgb(255, 50, 200, 255).ToArgb();
    public int TeamColor { get; set; } = Color.FromArgb(255, 255, 200, 50).ToArgb();
    public int SkeletonColor { get; set; } = Color.White.ToArgb();
    public int NameColor { get; set; } = Color.White.ToArgb();
    public int WeaponColor { get; set; } = Color.LightGray.ToArgb();
    public int MoneyColor { get; set; } = Color.Gold.ToArgb();

    public int MenuX { get; set; } = 100;
    public int MenuY { get; set; } = 100;

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "Cs2External_config.json"), json);
        }
        catch { }
    }

    public static Config Load()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "Cs2External_config.json");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
        }
        catch { }
        return new Config();
    }
}
