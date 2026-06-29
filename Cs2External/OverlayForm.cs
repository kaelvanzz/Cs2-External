using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;
using System.Windows.Forms;

namespace Cs2External;

public class OverlayForm : Form
{
    Config config;
    System.Windows.Forms.Timer renderTimer;
    Font espFont, espFontBold;
    bool running = true;

    public OverlayForm(Config cfg)
    {
        config = cfg;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Text = "CS2 Overlay";
        ShowInTaskbar = false;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;

        var screen = Screen.PrimaryScreen.Bounds;
        Bounds = screen;

        espFont = new Font("Consolas", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        espFontBold = new Font("Consolas", 11, FontStyle.Bold, GraphicsUnit.Pixel);

        renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
        renderTimer.Tick += (_, _) => { if (running) Invalidate(); };
        renderTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x80 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!running) return;
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // always draw a small debug indicator
        var snap = MainForm.CurrentSnapshot;
        if (snap == null)
        {
            g.DrawString("Waiting for game data...", espFont, Brushes.Gray, 10, 10);
            return;
        }
        if (snap.Players.Count == 0)
        {
            g.DrawString($"Connected | No players found (map: {snap.MapName})", espFont, Brushes.Gray, 10, 10);
            return;
        }
        g.DrawString($"{snap.Players.Count} players | {snap.MapName}", espFont, Brushes.Lime, Width - 200, 10);

        DrawESP(g, snap);
    }

    void DrawESP(Graphics g, MainForm.GameSnapshot snap)
    {
        var vm = snap.ViewMatrix;
        int gw = snap.GameW > 0 && snap.GameW < 10000 ? snap.GameW : Width;
        int gh = snap.GameH > 0 && snap.GameH < 10000 ? snap.GameH : Height;

        foreach (var p in snap.Players)
        {
            if (!p.Alive) continue;
            if (config.TeamCheck && p.Team == snap.LocalTeam) continue;

            var pos = new Vector3(p.X, p.Y, p.Z);
            if (!WorldToScreen(pos, vm, gw, gh, out float sx, out float sy)) continue;

            float dist = MathF.Sqrt(
                (p.X - snap.LocalPos.X) * (p.X - snap.LocalPos.X) +
                (p.Y - snap.LocalPos.Y) * (p.Y - snap.LocalPos.Y) +
                (p.Z - snap.LocalPos.Z) * (p.Z - snap.LocalPos.Z));
            if (dist < 1f) continue;

            float boxH = Math.Clamp(18000f / dist, 20f, 400f);
            float boxW = boxH * 0.6f;
            float hw = boxW / 2, hh = boxH / 2;

            bool isEnemy = p.Team != snap.LocalTeam;
            Color boxColor = Color.FromArgb(isEnemy ? config.EnemyColor : config.TeamColor);

            float x = sx, y = sy;

            if (config.BoxEnabled)
            {
                using var pen = new Pen(boxColor, 1.5f);
                var r = new RectangleF(x - hw, y - hh, boxW, boxH);
                if (config.BoxType == 0)
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                else if (config.BoxType == 1)
                {
                    float c = hw * 0.25f;
                    g.DrawLine(pen, x - hw, y - hh, x - hw + c, y - hh);
                    g.DrawLine(pen, x - hw, y - hh, x - hw, y - hh + c);
                    g.DrawLine(pen, x + hw - c, y - hh, x + hw, y - hh);
                    g.DrawLine(pen, x + hw, y - hh, x + hw, y - hh + c);
                    g.DrawLine(pen, x - hw, y + hh - c, x - hw, y + hh);
                    g.DrawLine(pen, x - hw, y + hh, x - hw + c, y + hh);
                    g.DrawLine(pen, x + hw - c, y + hh, x + hw, y + hh);
                    g.DrawLine(pen, x + hw, y + hh - c, x + hw, y + hh);
                }
                else
                {
                    using var fill = new SolidBrush(Color.FromArgb(96, boxColor.R, boxColor.G, boxColor.B));
                    g.FillRectangle(fill, r);
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
            }

            if (config.HealthBarEnabled)
            {
                float bx = x - hw - 6, by = y - hh, bw = 4, bh = boxH;
                float fill = bh * Math.Clamp(p.Health / 100f, 0f, 1f);
                Color hpColor = p.Health > 70 ? Color.Lime : p.Health > 35 ? Color.Yellow : Color.Red;
                g.FillRectangle(Brushes.Black, bx, by, bw, bh);
                g.FillRectangle(new SolidBrush(hpColor), bx, by + bh - fill, bw, fill);
                g.DrawRectangle(Pens.Black, bx, by, bw, bh);
            }

            using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            if (config.NameEnabled && !string.IsNullOrEmpty(p.Name))
                g.DrawString(p.Name, espFontBold, Brushes.White, x - hw, y - hh - 14, sf);

            if (config.WeaponEnabled)
            {
                string wn = WeaponName(p.WeaponId);
                if (!string.IsNullOrEmpty(wn))
                    g.DrawString(wn, espFont, Brushes.LightGray, x - hw, y + hh + 2, sf);
            }

            if (config.MoneyEnabled)
            {
                string ms = p.Money < 0 ? "$0" : $"${p.Money}";
                var sz = g.MeasureString(ms, espFont);
                g.DrawString(ms, espFont, Brushes.Gold, x + hw - sz.Width, y + hh + 2, sf);
            }

            if (config.DistanceEnabled)
                g.DrawString($"{dist:F0}m", espFont, Brushes.Silver, x - hw, y + hh + 16, sf);

            if (config.SkeletonEnabled && p.Bones != null && p.Bones.Length > 0)
            {
                using var skelPen = new Pen(Color.FromArgb(config.SkeletonColor), 1.5f);
                foreach (var (i, j) in BoneLinks)
                {
                    if (i >= p.Bones.Length || j >= p.Bones.Length) continue;
                    if (WorldToScreen(p.Bones[i], vm, gw, gh, out float x1, out float y1) &&
                        WorldToScreen(p.Bones[j], vm, gw, gh, out float x2, out float y2))
                        g.DrawLine(skelPen, x1, y1, x2, y2);
                }
            }
        }
    }

    static readonly (int, int)[] BoneLinks = new[]
    {
        (5,4),(4,3),(3,2),(2,1),(1,0),
        (4,6),(6,7),(7,8),
        (4,9),(9,10),(10,11),
        (0,12),(12,13),(13,14),
        (0,15),(15,16),(16,17),
    };

    static bool WorldToScreen(Vector3 world, float[] vm, int w, int h, out float sx, out float sy)
    {
        sx = sy = 0;
        float w2 = vm[3] * world.X + vm[7] * world.Y + vm[11] * world.Z + vm[15];
        if (w2 < 0.01f) return false;
        float inv = 1f / w2;
        float x = (vm[0] * world.X + vm[4] * world.Y + vm[8] * world.Z + vm[12]) * inv;
        float y = (vm[1] * world.X + vm[5] * world.Y + vm[9] * world.Z + vm[13]) * inv;
        float sxr = (w * 0.5f) * (1f + x);
        float syr = (h * 0.5f) * (1f - y);
        sx = sxr;
        sy = syr;
        return sxr >= 0 && sxr <= w && syr >= 0 && syr <= h;
    }

    static string WeaponName(int id) => id switch
    {
        1 => "Deagle", 2 => "Dual Berettas", 3 => "Five-SeveN", 4 => "Glock-18",
        7 => "AK-47", 8 => "AUG", 9 => "AWP", 10 => "FAMAS",
        11 => "G3SG1", 13 => "Galil AR", 14 => "M249", 16 => "M4A4",
        17 => "M4A1-S", 19 => "MP5-SD", 23 => "MP7", 24 => "MP9",
        25 => "Negev", 26 => "P90", 27 => "PP-Bizon", 28 => "SG 553",
        29 => "SCAR-20", 30 => "SSG 08", 31 => "Tec-9", 32 => "UMP-45",
        33 => "USP-S", 34 => "XM1014", 35 => "MAC-10", 36 => "MAG-7",
        40 => "P250", 41 => "CZ75-Auto", 46 => "Revolver", 47 => "Taser",
        48 => "HE", 49 => "Flash", 50 => "Smoke", 51 => "Molotov",
        52 => "Decoy", 53 => "Incendiary",
        _ => ""
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            running = false;
            renderTimer?.Dispose();
            espFont?.Dispose();
            espFontBold?.Dispose();
        }
        base.Dispose(disposing);
    }
}
