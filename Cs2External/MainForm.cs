using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Cs2External;

public partial class MainForm : Form
{
    #region Win32 P/Invoke
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll")]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll")]
    static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll")]
    static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
    [DllImport("kernel32.dll")]
    static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W { public uint dwSize; public uint cntUsage; public int th32ProcessID;
        public IntPtr th32DefaultHeapID; public uint th32ModuleID; public uint cntThreads;
        public int th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MODULEENTRY32W { public uint dwSize; public uint th32ModuleID; public int th32ProcessID;
        public uint GlblcntUsage; public uint ProccntUsage; public IntPtr modBaseAddr;
        public uint modBaseSize; public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath; }
    #endregion

    #region Constants
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint TH32CS_SNAPPROCESS = 0x0002;
    const uint TH32CS_SNAPMODULE = 0x0008;
    const uint TH32CS_SNAPMODULE32 = 0x0010;
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;
    const int VK_INSERT = 0x2D;

    const nint dwEntityList = 0x24E76A0;
    const nint dwLocalPlayerPawn = 0x2341698;
    const nint dwLocalPlayerController = 0x2320720;
    const nint dwViewMatrix = 0x2346B30;
    const nint dwGlobalVars = 0x20616D0;
    const nint dwWindowWidth = 0x90E5C0;
    const nint dwWindowHeight = 0x90E5C4;
    const nint m_hPlayerPawn = 0x90C;
    const nint m_iszPlayerName = 0x6F4;
    const nint m_iHealth = 0x34C;
    const nint m_lifeState = 0x354;
    const nint m_iTeamNum = 0x3EB;
    const nint m_vOldOrigin = 0x1390;
    const nint m_pGameSceneNode = 0x330;
    const nint m_modelState = 0x150;
    const nint m_pWeaponServices = 0x11E0;
    const nint m_hActiveWeapon = 0x60;
    const nint m_pInGameMoneyServices = 0x808;
    const nint m_iAccount = 0x40;
    const nint m_AttributeManager = 0x1180;
    const nint m_Item = 0x50;
    const nint m_iItemDefinitionIndex = 0x1BA;
    #endregion

    #region Player Data
    public class PlayerJson
    {
        public string Name { get; set; } = "";
        public float X { get; set; } public float Y { get; set; } public float Z { get; set; }
        public int Health { get; set; } public int Team { get; set; }
        public bool IsLocal { get; set; } public bool Alive { get; set; }
        public int WeaponId { get; set; } = -1;
        public int Money { get; set; } = -1;
        public Vector3[]? Bones { get; set; }
    }

    public class GameSnapshot
    {
        public string MapName { get; set; } = "";
        public Vector3 LocalPos { get; set; }
        public int LocalTeam { get; set; }
        public List<PlayerJson> Players { get; set; } = new();
        public float[] ViewMatrix { get; set; } = new float[16];
        public int GameW { get; set; }
        public int GameH { get; set; }
    }

    static GameSnapshot? s_snapshot;
    static readonly object s_snapLock = new();
    public static GameSnapshot? CurrentSnapshot { get { lock (s_snapLock) return s_snapshot; } }
    #endregion

    #region Backend State
    IntPtr g_process = IntPtr.Zero;
    int g_cs2Pid;
    IntPtr g_clientBase = IntPtr.Zero;
    IntPtr g_engineBase = IntPtr.Zero;
    bool g_backendRunning = true;
    CancellationTokenSource? g_gameCts;
    CancellationTokenSource? g_httpCts;
    HttpListener? g_listener;
    bool g_httpRunning;
    OverlayForm? g_overlay;
    Config g_config = Config.Load();
    #endregion

    #region UI Controls
    NotifyIcon trayIcon;
    ContextMenuStrip trayMenu;
    Label lblStatus, lblMap, lblPlayers;
    Button btnHttp, btnRadar, btnExit;
    System.Windows.Forms.Timer statusTimer, keyTimer;

    CheckBox chkEsp, chkTeamCheck, chkBox, chkHealth, chkSkeleton;
    CheckBox chkName, chkWeapon, chkMoney, chkDistance;
    ComboBox cmbBoxStyle;
    Button btnEnemyColor, btnTeamColor, btnSkeletonColor, btnNameColor, btnWeaponColor, btnMoneyColor;
    #endregion

    public MainForm()
    {
        Text = "CS2 External";
        Size = new Size(420, 540);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Icon = SystemIcons.Application;

        BuildUI();

        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
        trayMenu.Items.Add("Exit", null, (_, _) => Shutdown());
        trayIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "CS2 External", ContextMenuStrip = trayMenu, Visible = true };
        trayIcon.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; };

        statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        statusTimer.Tick += UpdateStatus;
        statusTimer.Start();

        keyTimer = new System.Windows.Forms.Timer { Interval = 100 };
        keyTimer.Tick += CheckKeys;
        keyTimer.Start();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
                Shutdown();
        };

        _ = TryConnectAsync();
    }

    void BuildUI()
    {
        var mainPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };
        int y = 0;

        void AddLabel(string text, int row, int col = 0, int w = 380)
        {
            var lbl = new Label { Text = text, Location = new Point(12 + col * 190, 8 + row * 28), Size = new Size(w, 22) };
            mainPanel.Controls.Add(lbl);
        }
        void AddCheck(string text, ref CheckBox cb, int row, int col = 0, int w = 180)
        {
            cb = new CheckBox { Text = text, Location = new Point(12 + col * 190, 6 + row * 28), Size = new Size(w, 24), UseVisualStyleBackColor = true };
            mainPanel.Controls.Add(cb);
        }

        AddLabel("CS2 External - Control Panel", y++, 0, 380);
        y++; // spacer

        AddLabel("Status", y++);
        lblStatus = new Label { Text = "Disconnected", ForeColor = Color.Red, Location = new Point(90, 8 + 2 * 28), Size = new Size(280, 22) };
        mainPanel.Controls.Add(lblStatus);
        lblMap = new Label { Text = "Map: --", Location = new Point(12, 8 + 3 * 28), Size = new Size(180, 22) };
        mainPanel.Controls.Add(lblMap);
        lblPlayers = new Label { Text = "Players: 0", Location = new Point(200, 8 + 3 * 28), Size = new Size(180, 22) };
        mainPanel.Controls.Add(lblPlayers);

        y = 5;
        AddLabel("ESP Settings", y++);
        AddCheck("ESP", ref chkEsp, y++, 0); chkEsp.CheckedChanged += (_, _) => { g_config.EspEnabled = chkEsp.Checked; if (chkEsp.Checked) StartOverlay(); else StopOverlay(); }; chkEsp.Checked = g_config.EspEnabled;
        AddCheck("Team Check", ref chkTeamCheck, y++, 0); chkTeamCheck.Checked = g_config.TeamCheck; chkTeamCheck.CheckedChanged += (_, _) => g_config.TeamCheck = chkTeamCheck.Checked;
        AddCheck("Box", ref chkBox, y, 0); chkBox.Checked = g_config.BoxEnabled; chkBox.CheckedChanged += (_, _) => g_config.BoxEnabled = chkBox.Checked;
        cmbBoxStyle = new ComboBox { Location = new Point(120, 6 + y * 28), Size = new Size(100, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbBoxStyle.Items.AddRange(new[] { "2D", "Corner", "Filled" });
        cmbBoxStyle.SelectedIndex = g_config.BoxType;
        cmbBoxStyle.SelectedIndexChanged += (_, _) => g_config.BoxType = cmbBoxStyle.SelectedIndex;
        mainPanel.Controls.Add(cmbBoxStyle);
        y++;
        AddCheck("Health Bar", ref chkHealth, y++, 0); chkHealth.Checked = g_config.HealthBarEnabled; chkHealth.CheckedChanged += (_, _) => g_config.HealthBarEnabled = chkHealth.Checked;
        AddCheck("Skeleton", ref chkSkeleton, y++, 0); chkSkeleton.Checked = g_config.SkeletonEnabled; chkSkeleton.CheckedChanged += (_, _) => g_config.SkeletonEnabled = chkSkeleton.Checked;
        AddCheck("Name", ref chkName, y++, 0); chkName.Checked = g_config.NameEnabled; chkName.CheckedChanged += (_, _) => g_config.NameEnabled = chkName.Checked;
        AddCheck("Weapon", ref chkWeapon, y++, 0); chkWeapon.Checked = g_config.WeaponEnabled; chkWeapon.CheckedChanged += (_, _) => g_config.WeaponEnabled = chkWeapon.Checked;
        AddCheck("Money", ref chkMoney, y++, 0); chkMoney.Checked = g_config.MoneyEnabled; chkMoney.CheckedChanged += (_, _) => g_config.MoneyEnabled = chkMoney.Checked;
        AddCheck("Distance", ref chkDistance, y++, 0); chkDistance.Checked = g_config.DistanceEnabled; chkDistance.CheckedChanged += (_, _) => g_config.DistanceEnabled = chkDistance.Checked;

        y++;
        AddLabel("Colors", y++);
        int cy = y;
        int cs = 28;
        Button MakeColorBtn(string display, string prop, int x, int yPos, int color)
        {
            var lbl = new Label { Text = display, Location = new Point(x, yPos), Size = new Size(70, 22) };
            mainPanel.Controls.Add(lbl);
            int curColor = color;
            var btn = new Button { Location = new Point(x + 73, yPos - 2), Size = new Size(cs, cs), FlatStyle = FlatStyle.Flat };
            btn.BackColor = Color.FromArgb(curColor);
            btn.Click += (_, _) =>
            {
                using var cd = new ColorDialog { Color = btn.BackColor, FullOpen = true };
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    btn.BackColor = cd.Color;
                    curColor = cd.Color.ToArgb();
                    typeof(Config).GetProperty(prop)?.SetValue(g_config, curColor);
                }
            };
            mainPanel.Controls.Add(btn);
            return btn;
        }
        int bx = 12;
        btnEnemyColor = MakeColorBtn("Enemy", "EnemyColor", bx, 8 + cy * cs, g_config.EnemyColor);
        btnTeamColor = MakeColorBtn("Team", "TeamColor", bx, 8 + (cy + 1) * cs, g_config.TeamColor);
        btnSkeletonColor = MakeColorBtn("Skeleton", "SkeletonColor", bx, 8 + (cy + 2) * cs, g_config.SkeletonColor);
        btnNameColor = MakeColorBtn("Name", "NameColor", bx + 190, 8 + cy * cs, g_config.NameColor);
        btnWeaponColor = MakeColorBtn("Weapon", "WeaponColor", bx + 190, 8 + (cy + 1) * cs, g_config.WeaponColor);
        btnMoneyColor = MakeColorBtn("Money", "MoneyColor", bx + 190, 8 + (cy + 2) * cs, g_config.MoneyColor);

        y = cy + 4;
        y++;
        btnHttp = new Button { Text = "Start HTTP", Location = new Point(12, 8 + y * 28), Size = new Size(110, 30) };
        btnHttp.Click += ToggleHttp;
        mainPanel.Controls.Add(btnHttp);

        btnRadar = new Button { Text = "Open Radar", Location = new Point(135, 8 + y * 28), Size = new Size(90, 30) };
        btnRadar.Click += (_, _) => Process.Start(new ProcessStartInfo("http://localhost:5678/") { UseShellExecute = true });
        mainPanel.Controls.Add(btnRadar);

        y++;
        btnExit = new Button { Text = "Exit", Location = new Point(12, 8 + y * 28), Size = new Size(80, 28) };
        btnExit.Click += (_, _) => Shutdown();
        mainPanel.Controls.Add(btnExit);

        var hint = new Label { Text = "Insert key: show/hide overlay", Location = new Point(110, 8 + y * 28), Size = new Size(250, 22), ForeColor = Color.Gray };
        mainPanel.Controls.Add(hint);

        Controls.Add(mainPanel);
    }

    #region Connect / Disconnect
    async Task TryConnectAsync()
    {
        while (g_backendRunning)
        {
            if (g_process == IntPtr.Zero)
            {
                int pid = FindCs2Pid();
                if (pid != 0)
                {
                    g_cs2Pid = pid;
                    g_process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
                    if (g_process != IntPtr.Zero)
                    {
                        g_clientBase = GetModuleBase(pid, "client.dll");
                        g_engineBase = GetModuleBase(pid, "engine2.dll");
                        if (g_clientBase != IntPtr.Zero && g_engineBase != IntPtr.Zero)
                        {
                            BeginInvoke(() => { lblStatus.Text = $"Connected (PID {pid})"; lblStatus.ForeColor = Color.LimeGreen; if (chkEsp.Checked) StartOverlay(); });
                            StartGameLoop();
                        }
                    }
                }
            }
            await Task.Delay(3000);
        }
    }

    int FindCs2Pid()
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero) return 0;
        var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        int pid = 0;
        if (Process32FirstW(snap, ref pe))
            do { if (pe.szExeFile.Contains("cs2", StringComparison.OrdinalIgnoreCase)) { pid = pe.th32ProcessID; break; } }
            while (Process32NextW(snap, ref pe));
        CloseHandle(snap);
        return pid;
    }

    IntPtr GetModuleBase(int pid, string moduleName)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == IntPtr.Zero) return IntPtr.Zero;
        var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
        IntPtr result = IntPtr.Zero;
        if (Module32FirstW(snap, ref me))
            do { if (me.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) { result = me.modBaseAddr; break; } }
            while (Module32NextW(snap, ref me));
        CloseHandle(snap);
        return result;
    }
    #endregion

    #region Memory Reading
    unsafe T Read<T>(IntPtr address) where T : unmanaged
    {
        byte[] buf = new byte[sizeof(T)];
        ReadProcessMemory(g_process, address, buf, buf.Length, out _);
        fixed (byte* p = buf) return *(T*)p;
    }
    byte[] ReadBytes(IntPtr address, int size)
    {
        byte[] buf = new byte[size];
        ReadProcessMemory(g_process, address, buf, size, out _);
        return buf;
    }
    string ReadString(IntPtr address, int maxLen)
    {
        byte[] buf = ReadBytes(address, maxLen);
        int len = 0;
        while (len < maxLen && buf[len] != 0) len++;
        return Encoding.UTF8.GetString(buf, 0, len);
    }
    IntPtr GetEntityFromIndex(IntPtr entitySystem, int index)
    {
        int listIdx = index >> 9;
        int entryIdx = index & 0x1FF;
        IntPtr bucket = Read<IntPtr>(entitySystem + 0x10 + listIdx * 8);
        if (bucket == IntPtr.Zero) return IntPtr.Zero;
        return Read<IntPtr>(bucket + entryIdx * 0x78);
    }
    #endregion

    #region Game Loop
    void StartGameLoop()
    {
        g_gameCts?.Cancel();
        g_gameCts = new CancellationTokenSource();
        _ = Task.Run(() => GameLoop(g_gameCts.Token));
    }

    async Task GameLoop(CancellationToken ct)
    {
        int scrW = GetSystemMetrics(SM_CXSCREEN);
        int scrH = GetSystemMetrics(SM_CYSCREEN);

        while (!ct.IsCancellationRequested && g_process != IntPtr.Zero)
        {
            try
            {
                byte[] vmBytes = ReadBytes(g_clientBase + dwViewMatrix, 64);
                float[] vm = new float[16];
                Buffer.BlockCopy(vmBytes, 0, vm, 0, 64);

                IntPtr localPawn = Read<IntPtr>(g_clientBase + dwLocalPlayerPawn);
                IntPtr localController = Read<IntPtr>(g_clientBase + dwLocalPlayerController);
                Vector3 localPos = default;
                int localTeam = 0;

                if (localPawn != IntPtr.Zero)
                {
                    localPos = Read<Vector3>(localPawn + m_vOldOrigin);
                    localTeam = Read<int>(localPawn + m_iTeamNum);
                }

                IntPtr entitySystem = Read<IntPtr>(g_clientBase + dwEntityList);
                int gameW = Read<int>(g_engineBase + dwWindowWidth);
                int gameH = Read<int>(g_engineBase + dwWindowHeight);
                if (gameW <= 0 || gameW > 10000) gameW = scrW;
                if (gameH <= 0 || gameH > 10000) gameH = scrH;

                var players = new List<PlayerJson>();

                if (entitySystem != IntPtr.Zero)
                {
                    for (int i = 1; i <= 64; i++)
                    {
                        IntPtr controller = GetEntityFromIndex(entitySystem, i);
                        if (controller == IntPtr.Zero) continue;
                        uint pawnHandle = Read<uint>(controller + m_hPlayerPawn);
                        if (pawnHandle == 0 || pawnHandle == 0xFFFFFFFF) continue;
                        int pawnIdx = (int)(pawnHandle & 0x7FFF);
                        IntPtr pawn = GetEntityFromIndex(entitySystem, pawnIdx);
                        if (pawn == IntPtr.Zero) continue;
                        string name = ReadString(controller + m_iszPlayerName, 128);
                        if (string.IsNullOrEmpty(name)) continue;
                        int health = Read<int>(pawn + m_iHealth);
                        int lifeState = Read<int>(pawn + m_lifeState);
                        int team = Read<int>(pawn + m_iTeamNum);
                        var pos = Read<Vector3>(pawn + m_vOldOrigin);

                        int weaponId = ReadWeaponId(pawn);
                        int money = ReadMoney(controller, pawn);
                        var bones = ReadBones(pawn);

                        players.Add(new PlayerJson
                        {
                            Name = name, X = pos.X, Y = pos.Y, Z = pos.Z,
                            Health = health, Team = team,
                            IsLocal = (pawn == localPawn), Alive = health > 0 && lifeState == 0,
                            WeaponId = weaponId, Money = money, Bones = bones
                        });
                    }
                }

                string mapName = "";
                IntPtr globalVars = Read<IntPtr>(g_clientBase + dwGlobalVars);
                if (globalVars != IntPtr.Zero) mapName = ReadString(globalVars + 0x188, 128);

                lock (s_snapLock)
                {
                    s_snapshot = new GameSnapshot
                    {
                        MapName = mapName,
                        LocalPos = localPos,
                        LocalTeam = localTeam,
                        Players = players,
                        ViewMatrix = vm,
                        GameW = gameW, GameH = gameH
                    };
                }
            }
            catch
            {
                lock (s_snapLock) { s_snapshot = new GameSnapshot(); }
            }

            await Task.Delay(16, ct);
        }
    }

    int ReadWeaponId(IntPtr pawn)
    {
        try
        {
            IntPtr weaponServices = Read<IntPtr>(pawn + m_pWeaponServices);
            if (weaponServices == IntPtr.Zero) return -1;
            uint activeHandle = Read<uint>(weaponServices + m_hActiveWeapon);
            if (activeHandle == 0 || activeHandle == 0xFFFFFFFF) return -1;
            int weaponIdx = (int)(activeHandle & 0x7FFF);
            if (weaponIdx == 0) return -1;
            IntPtr entitySystem = Read<IntPtr>(g_clientBase + dwEntityList);
            if (entitySystem == IntPtr.Zero) return -1;
            IntPtr weapon = GetEntityFromIndex(entitySystem, weaponIdx);
            if (weapon == IntPtr.Zero) return -1;
            return Read<ushort>(weapon + m_AttributeManager + m_Item + m_iItemDefinitionIndex);
        }
        catch { return -1; }
    }

    int ReadMoney(IntPtr controller, IntPtr pawn)
    {
        try
        {
            IntPtr moneyServices = Read<IntPtr>(controller + m_pInGameMoneyServices);
            if (moneyServices == IntPtr.Zero) return -1;
            return Read<int>(moneyServices + m_iAccount);
        }
        catch { return -1; }
    }

    Vector3[]? ReadBones(IntPtr pawn)
    {
        try
        {
            IntPtr sceneNode = Read<IntPtr>(pawn + m_pGameSceneNode);
            if (sceneNode == IntPtr.Zero) return null;
            IntPtr modelStateAddr = sceneNode + m_modelState;
            IntPtr boneArray = Read<IntPtr>(modelStateAddr + 0x80);
            if (boneArray == IntPtr.Zero) return null;

            var bones = new Vector3[64];
            for (int i = 0; i < 64; i++)
            {
                float px = Read<float>(boneArray + i * 48 + 12);
                float py = Read<float>(boneArray + i * 48 + 13);
                float pz = Read<float>(boneArray + i * 48 + 14);
                bones[i] = new Vector3(px, py, pz);
            }
            return bones;
        }
        catch { return null; }
    }
    #endregion

    #region Overlay
    void StartOverlay()
    {
        if (g_process == IntPtr.Zero) return;
        if (g_overlay != null) return;
        g_overlay = new OverlayForm(g_config);
        g_overlay.FormClosed += (_, _) => { if (g_overlay != null) { g_overlay = null; chkEsp.Checked = false; } };
        g_overlay.Show();
    }

    void StopOverlay()
    {
        if (g_overlay == null) return;
        var overlay = g_overlay;
        g_overlay = null;
        overlay.Close();
        overlay.Dispose();
        g_config.Save();
    }
    #endregion

    #region HTTP
    void ToggleHttp(object? sender, EventArgs e)
    {
        if (g_httpRunning) StopHttp();
        else StartHttp();
    }

    void StartHttp()
    {
        g_httpCts = new CancellationTokenSource();
        g_httpRunning = true;
        btnHttp.Text = "Stop HTTP";
        _ = Task.Run(() => HttpLoop(g_httpCts.Token));
    }

    void StopHttp()
    {
        g_httpCts?.Cancel();
        try { g_listener?.Stop(); } catch { }
        g_httpRunning = false;
        btnHttp.Text = "Start HTTP";
    }

    async Task HttpLoop(CancellationToken ct)
    {
        g_listener = new HttpListener();
        g_listener.Prefixes.Add("http://localhost:5678/");
        try { g_listener.Start(); }
        catch (Exception ex) { BeginInvoke(() => MessageBox.Show($"HTTP server failed: {ex.Message}")); return; }

        while (!ct.IsCancellationRequested)
        {
            var ctxTask = g_listener.GetContextAsync();
            var delay = Task.Delay(500, ct);
            var done = await Task.WhenAny(ctxTask, delay);
            if (ct.IsCancellationRequested) break;
            if (done != ctxTask) continue;
            try { await HandleRequest(ctxTask.Result); }
            catch { }
        }
        try { g_listener.Stop(); } catch { }
    }

    async Task HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path == "/api/players")
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.AppendHeader("Access-Control-Allow-Origin", "*");
            var snap = CurrentSnapshot;
            var json = JsonSerializer.Serialize(new
            {
                map = snap?.MapName ?? "",
                gameW = snap?.GameW ?? 0, gameH = snap?.GameH ?? 0,
                localTeam = snap?.LocalTeam ?? 0,
                localPos = new { x = snap?.LocalPos.X ?? 0, y = snap?.LocalPos.Y ?? 0, z = snap?.LocalPos.Z ?? 0 },
                players = snap?.Players ?? new List<PlayerJson>()
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            byte[] buf = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
        else
        {
            string html = RadarHtml;
            byte[] buf = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
    }
    #endregion

    #region Status & Keys
    void UpdateStatus(object? sender, EventArgs e)
    {
        if (g_process == IntPtr.Zero)
        {
            lblStatus.Text = "Disconnected"; lblStatus.ForeColor = Color.Red;
            lblMap.Text = "Map: --"; lblPlayers.Text = "Players: 0";
        }
        else
        {
            var snap = CurrentSnapshot;
            if (snap != null)
            {
                lblMap.Text = string.IsNullOrEmpty(snap.MapName) ? "Map: --" : $"Map: {snap.MapName}";
                int alive = 0;
                foreach (var p in snap.Players) if (p.Alive) alive++;
                lblPlayers.Text = $"{snap.Players.Count} total, {alive} alive";
            }
        }
    }

    void CheckKeys(object? sender, EventArgs e)
    {
        if ((GetAsyncKeyState(VK_INSERT) & 1) != 0)
        {
            if (Visible)
                Hide();
            else
            { Show(); WindowState = FormWindowState.Normal; BringToFront(); }
        }
    }
    #endregion

    #region Shutdown
    void Shutdown()
    {
        g_backendRunning = false;
        StopOverlay();
        StopHttp();
        statusTimer.Stop();
        keyTimer.Stop();
        trayIcon.Visible = false;
        g_config.Save();
        if (g_process != IntPtr.Zero) CloseHandle(g_process);
        Application.Exit();
    }
    #endregion

    #region Radar HTML
    static readonly string RadarHtml = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>CS2 Radar</title>
<style>*{margin:0;padding:0;box-sizing:border-box}body{background:#0a0a12;color:#ccc;font-family:monospace;overflow:hidden;height:100vh;display:flex}
#rc{flex:1;display:flex;align-items:center;justify-content:center}#radar{border:2px solid #0af;border-radius:50%;background:#14141e}
#info{position:fixed;top:15px;left:15px;z-index:10;color:#0af;font-size:18px}#info p{color:#888;font-size:13px}
#pl{width:280px;background:#0e0e18;border-left:1px solid #1a1a2a;padding:15px;overflow-y:auto;height:100vh}
#pl h3{color:#0af;margin-bottom:10px;font-size:14px}.pr{display:flex;align-items:center;padding:6px 8px;border-bottom:1px solid #181824;font-size:13px}
.pd{width:10px;height:10px;border-radius:50%;margin-right:8px;flex-shrink:0}.pn{flex:1;color:#ddd}
.ph{width:40px;text-align:right;margin-right:10px}.pd2{width:60px;text-align:right;color:#888}
</style></head><body>
<div id='info'><h2 id='mn'>--</h2><p id='st'>Waiting</p></div>
<div id='rc'><canvas id='radar'></canvas></div>
<div id='pl'><h3>Players</h3><div id='ps'>--</div></div>
<script>
const c=document.getElementById('radar'),x=c.getContext('2d'),R=350;c.width=R*2;c.height=R*2;
setInterval(()=>{fetch('/api/players').then(r=>r.json()).then(d=>{
document.getElementById('mn').textContent=d.map||'--';
document.getElementById('st').textContent=d.players.length+' players|'+new Date().toLocaleTimeString();
x.clearRect(0,0,R*2,R*2);
x.strokeStyle='#1a1a2a';x.lineWidth=1;[0.25,0.5,0.75].forEach(f=>{x.beginPath();x.arc(R,R,R*f,0,Math.PI*2);x.stroke()});
x.strokeStyle='#252535';x.lineWidth=1;x.beginPath();x.moveTo(R,0);x.lineTo(R,R*2);x.stroke();
x.beginPath();x.moveTo(0,R);x.lineTo(R*2,R);x.stroke();
x.strokeStyle='#0af';x.lineWidth=2;x.beginPath();x.arc(R,R,R,0,Math.PI*2);x.stroke();
let lx=d.localPos.x,ly=d.localPos.y,h='';
d.players.forEach(p=>{let dx=p.x-lx,dy=p.y-ly,m=Math.hypot(dx,dy);if(m>2000||m<1)return;
let s=(R-12)/2000,sx=R+dx*s,sy=R+dy*s;
let cl=p.team==2?'#ffc832':p.team==3?'#32c8ff':'#444';if(p.isLocal)cl='#0f0';if(!p.alive){cl='#444';x.globalAlpha=0.3}
x.beginPath();x.arc(sx,sy,5,0,Math.PI*2);x.fillStyle=cl;x.fill();x.globalAlpha=1;
let hc=p.health>70?'#0f0':p.health>35?'#ff0':'#f00';
h+='<div class=pr><span class=pd style=background:'+cl+'></span><span class=pn>'+(p.isLocal?'[YOU] ':'')+p.name+'</span><span class=ph style=color:'+hc+'>'+p.health+'HP</span><span class=pd2>'+m.toFixed(0)+'m</span></div>';
});document.getElementById('ps').innerHTML=h||'<div style=color:#555;padding:10px>--</div>';
x.beginPath();x.arc(R,R,6,0,Math.PI*2);x.fillStyle='#0f0';x.fill();
}).catch(()=>{})},200);
</script></body></html>";
    #endregion
}
