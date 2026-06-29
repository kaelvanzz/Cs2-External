#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <vector>
#include <string>
#include <algorithm>
#include <cmath>
#include <cstdio>

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

// ================== OFFSETS (from generated dumps) ==================
namespace Offsets {
    // client.dll offsets
    constexpr uintptr_t dwEntityList = 0x24E76A0;
    constexpr uintptr_t dwLocalPlayerController = 0x2320720;
    constexpr uintptr_t dwLocalPlayerPawn = 0x2341698;
    constexpr uintptr_t dwViewMatrix = 0x2346B30;
    constexpr uintptr_t dwGlobalVars = 0x20616D0;
    constexpr uintptr_t dwGameEntitySystem_highestEntityIndex = 0x2090;

    // engine2.dll offsets
    constexpr uintptr_t dwNetworkGameClient = 0x90A1A0;
    constexpr uintptr_t dwWindowWidth = 0x90E5C0;
    constexpr uintptr_t dwWindowHeight = 0x90E5C4;

    // CCSPlayerController (CBaseEntity)
    constexpr uintptr_t m_hPlayerPawn = 0x90C;       // CHandle<C_CSPlayerPawn>
    constexpr uintptr_t m_iszPlayerName = 0x6F4;      // char[128]

    // C_BaseEntity / C_CSPlayerPawn
    constexpr uintptr_t m_pGameSceneNode = 0x330;      // CGameSceneNode*
    constexpr uintptr_t m_iHealth = 0x34C;             // int32
    constexpr uintptr_t m_lifeState = 0x354;           // uint8
    constexpr uintptr_t m_iTeamNum = 0x3EB;            // uint8
    constexpr uintptr_t m_vOldOrigin = 0x1390;         // Vector (C_CSPlayerPawn)
    constexpr uintptr_t m_vecAbsVelocity = 0x3FC;      // Vector
    constexpr uintptr_t m_bIsScoped = 0x1C50;          // bool

    // CGameSceneNode
    constexpr uintptr_t m_vecAbsOrigin = 0xC8;         // VectorWS
}

// ================== MATH HELPERS ==================
struct Vector3 {
    float x, y, z;
};

struct Matrix4x4 {
    float data[16];
};

bool WorldToScreen(const Vector3& world, Vector3& screen, const Matrix4x4& viewMatrix,
                   int screenW, int screenH) {
    float w = viewMatrix.data[3] * world.x + viewMatrix.data[7] * world.y
            + viewMatrix.data[11] * world.z + viewMatrix.data[15];
    if (w < 0.01f) return false;

    float invW = 1.0f / w;
    screen.x = (viewMatrix.data[0] * world.x + viewMatrix.data[4] * world.y
               + viewMatrix.data[8] * world.z + viewMatrix.data[12]) * invW;
    screen.y = (viewMatrix.data[1] * world.x + viewMatrix.data[5] * world.y
               + viewMatrix.data[9] * world.z + viewMatrix.data[13]) * invW;

    float x = (screenW * 0.5f) * (1.0f + screen.x);
    float y = (screenH * 0.5f) * (1.0f - screen.y);

    if (x < 0 || x > screenW || y < 0 || y > screenH) return false;

    screen.x = x;
    screen.y = y;
    return true;
}

float Distance3D(const Vector3& a, const Vector3& b) {
    float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrtf(dx * dx + dy * dy + dz * dz);
}

// ================== MEMORY & PROCESS ==================
HANDLE g_process = nullptr;
uintptr_t g_clientBase = 0;
uintptr_t g_engineBase = 0;

uintptr_t GetModuleBase(const wchar_t* moduleName) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
                                           GetProcessId(g_process));
    if (snap == INVALID_HANDLE_VALUE) return 0;

    MODULEENTRY32W entry;
    entry.dwSize = sizeof(entry);
    uintptr_t result = 0;

    if (Module32FirstW(snap, &entry)) {
        do {
            if (_wcsicmp(entry.szModule, moduleName) == 0) {
                result = (uintptr_t)entry.modBaseAddr;
                break;
            }
        } while (Module32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return result;
}

template<typename T>
T Read(uintptr_t addr) {
    T val = {};
    ReadProcessMemory(g_process, (LPCVOID)addr, &val, sizeof(T), nullptr);
    return val;
}

void ReadBuffer(uintptr_t addr, void* buf, size_t size) {
    ReadProcessMemory(g_process, (LPCVOID)addr, buf, size, nullptr);
}

struct PlayerData {
    uintptr_t controllerAddr;
    uintptr_t pawnAddr;
    Vector3 position;
    Vector3 velocity;
    int health;
    int team;
    std::wstring name;
    bool alive;
    bool isLocal;
};

// ================== OVERLAY WINDOW ==================
HWND g_overlayWnd = nullptr;
int g_screenW = 0, g_screenH = 0;

LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        case WM_PAINT:
            ValidateRect(hWnd, nullptr);
            return 0;
    }
    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

HWND CreateOverlay() {
    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CS2ExternalOverlay";
    RegisterClassW(&wc);

    g_screenW = GetSystemMetrics(SM_CXSCREEN);
    g_screenH = GetSystemMetrics(SM_CYSCREEN);

    HWND wnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW,
        L"CS2ExternalOverlay", L"",
        WS_POPUP,
        0, 0, g_screenW, g_screenH,
        nullptr, nullptr, wc.hInstance, nullptr
    );

    if (wnd) {
        SetLayeredWindowAttributes(wnd, RGB(0, 0, 0), 0, LWA_COLORKEY);
        ShowWindow(wnd, SW_SHOW);
    }
    return wnd;
}

// ================== RADAR CONFIG ==================
constexpr int RADAR_SIZE = 220;
constexpr int RADAR_MARGIN = 15;
constexpr float RADAR_RANGE = 2000.0f; // drawing range in game units
constexpr int RADAR_DOT_SIZE = 4;

void DrawRadar(HDC hdc, const Vector3& localPos, const std::vector<PlayerData>& players) {
    int rx = g_screenW - RADAR_SIZE - RADAR_MARGIN;
    int ry = g_screenH - RADAR_SIZE - RADAR_MARGIN;

    // Radar background
    HBRUSH bgBrush = CreateSolidBrush(RGB(20, 20, 30));
    HBRUSH oldBrush = (HBRUSH)SelectObject(hdc, bgBrush);
    HPEN borderPen = CreatePen(PS_SOLID, 2, RGB(0, 150, 255));
    HPEN oldPen = (HPEN)SelectObject(hdc, borderPen);
    Rectangle(hdc, rx, ry, rx + RADAR_SIZE, ry + RADAR_SIZE);
    SelectObject(hdc, oldPen);
    SelectObject(hdc, oldBrush);
    DeleteObject(bgBrush);
    DeleteObject(borderPen);

    // Cross lines
    HPEN crossPen = CreatePen(PS_SOLID, 1, RGB(60, 60, 80));
    oldPen = (HPEN)SelectObject(hdc, crossPen);
    MoveToEx(hdc, rx + RADAR_SIZE / 2, ry, nullptr);
    LineTo(hdc, rx + RADAR_SIZE / 2, ry + RADAR_SIZE);
    MoveToEx(hdc, rx, ry + RADAR_SIZE / 2, nullptr);
    LineTo(hdc, rx + RADAR_SIZE, ry + RADAR_SIZE / 2);
    SelectObject(hdc, oldPen);
    DeleteObject(crossPen);

    // Draw players
    for (const auto& p : players) {
        if (!p.alive) continue;
        float dx = p.position.x - localPos.x;
        float dy = p.position.y - localPos.y;
        float dist = std::sqrtf(dx * dx + dy * dy);
        if (dist > RADAR_RANGE || dist < 1.0f) continue;

        // Scale to radar
        float scale = (RADAR_SIZE / 2.0f - 8) / RADAR_RANGE;
        int sx = rx + RADAR_SIZE / 2 + (int)(dx * scale);
        int sy = ry + RADAR_SIZE / 2 + (int)(dy * scale);
        sx = std::clamp(sx, rx + 1, rx + RADAR_SIZE - 1);
        sy = std::clamp(sy, ry + 1, ry + RADAR_SIZE - 1);

        COLORREF color = (p.team == 2) ? RGB(255, 200, 50)   // T = yellow
                       : (p.team == 3) ? RGB(50, 150, 255)   // CT = blue
                       : RGB(255, 255, 255);
        if (p.isLocal) color = RGB(0, 255, 0);

        HBRUSH dotBrush = CreateSolidBrush(color);
        HBRUSH oldDb = (HBRUSH)SelectObject(hdc, dotBrush);
        Ellipse(hdc, sx - RADAR_DOT_SIZE, sy - RADAR_DOT_SIZE,
                sx + RADAR_DOT_SIZE, sy + RADAR_DOT_SIZE);
        SelectObject(hdc, oldDb);
        DeleteObject(dotBrush);
    }

    // Local player dot (green triangle/dot at center)
    HBRUSH localBrush = CreateSolidBrush(RGB(0, 255, 0));
    oldBrush = (HBRUSH)SelectObject(hdc, localBrush);
    int cx = rx + RADAR_SIZE / 2, cy = ry + RADAR_SIZE / 2;
    Ellipse(hdc, cx - RADAR_DOT_SIZE, cy - RADAR_DOT_SIZE,
            cx + RADAR_DOT_SIZE, cy + RADAR_DOT_SIZE);
    SelectObject(hdc, oldBrush);
    DeleteObject(localBrush);

    // Map label
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(200, 200, 200));
    TextOutW(hdc, rx + 5, ry - 18, L"RADAR", 5);
}

// ================== MAIN LOOP ==================
void GameLoop() {
    // Initialize overlay
    g_overlayWnd = CreateOverlay();
    if (!g_overlayWnd) {
        MessageBoxA(nullptr, "Failed to create overlay window!", "Error", MB_OK);
        return;
    }

    HDC hdcScreen = GetDC(nullptr);
    HDC hdcMem = CreateCompatibleDC(hdcScreen);
    HBITMAP hbmMem = CreateCompatibleBitmap(hdcScreen, g_screenW, g_screenH);
    SelectObject(hdcMem, hbmMem);

    // Fonts
    HFONT espFont = CreateFontW(14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
                                DEFAULT_PITCH | FF_DONTCARE, L"Consolas");
    HFONT radarLabelFont = CreateFontW(16, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                                        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                        CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
                                        DEFAULT_PITCH | FF_DONTCARE, L"Arial");

    MSG msg = {};
    bool running = true;

    while (running) {
        // Process messages
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) { running = false; break; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        // Clear background
        RECT cls;
        SetRect(&cls, 0, 0, g_screenW, g_screenH);
        HBRUSH blackBrush = (HBRUSH)GetStockObject(BLACK_BRUSH);
        FillRect(hdcMem, &cls, blackBrush);

        // Read data
        if (g_process && g_clientBase) {
            // Read view matrix
            Matrix4x4 viewMatrix = Read<Matrix4x4>(g_clientBase + Offsets::dwViewMatrix);

            // Read local player pawn
            uintptr_t localPawn = Read<uintptr_t>(g_clientBase + Offsets::dwLocalPlayerPawn);

            // Read game window dimensions from engine
            int gameW = Read<int>(g_engineBase + Offsets::dwWindowWidth);
            int gameH = Read<int>(g_engineBase + Offsets::dwWindowHeight);
            if (gameW <= 0 || gameH <= 0) { gameW = g_screenW; gameH = g_screenH; }

            // Read entity system
            uintptr_t entitySystem = Read<uintptr_t>(g_clientBase + Offsets::dwEntityList);
            if (!entitySystem) continue;

            // Read local player position
            Vector3 localPos = {};
            if (localPawn) {
                localPos = Read<Vector3>(localPawn + Offsets::m_vOldOrigin);
            }

            // Read local player team
            int localTeam = localPawn ? Read<int>(localPawn + Offsets::m_iTeamNum) : 0;

            // Collect player data
            std::vector<PlayerData> players;

            // Helper lambda: get entity address from index via 2-level entity list
            auto GetEntityFromIndex = [&](int index) -> uintptr_t {
                int listIdx = index >> 9;       // index / 512
                int entryIdx = index & 0x1FF;   // index % 512
                uintptr_t bucket = Read<uintptr_t>(entitySystem + 0x10 + listIdx * 8);
                if (!bucket) return 0;
                // The entity list stores CEntityIdentity structs by value (each 0x78 bytes)
                // First 8 bytes of each identity = CBaseEntity* pointer
                return Read<uintptr_t>(bucket + entryIdx * 0x78);
            };

            for (int i = 1; i <= 64; i++) {
                // Get CCSPlayerController for this index
                uintptr_t controller = GetEntityFromIndex(i);
                if (!controller) continue;

                // Read pawn handle from controller
                uint32_t pawnHandle = Read<uint32_t>(controller + Offsets::m_hPlayerPawn);
                if (!pawnHandle || pawnHandle == (uint32_t)-1) continue;

                // CHandle encoding: lower bits = entity list index
                int pawnIdx = pawnHandle & 0x7FFF;
                uintptr_t pawn = GetEntityFromIndex(pawnIdx);
                if (!pawn) continue;

                // Read name
                wchar_t nameBuf[128] = {};
                ReadBuffer(controller + Offsets::m_iszPlayerName, nameBuf, sizeof(nameBuf));

                // Wrong player - skip unnamed
                if (nameBuf[0] == 0) continue;

                // Read health, team, position
                int health = Read<int>(pawn + Offsets::m_iHealth);
                int lifeState = Read<int>(pawn + Offsets::m_lifeState);
                int team = Read<int>(pawn + Offsets::m_iTeamNum);
                Vector3 pos = Read<Vector3>(pawn + Offsets::m_vOldOrigin);
                Vector3 vel = Read<Vector3>(pawn + Offsets::m_vecAbsVelocity);

                PlayerData pd;
                pd.controllerAddr = controller;
                pd.pawnAddr = pawn;
                pd.position = pos;
                pd.velocity = vel;
                pd.health = health;
                pd.team = team;
                pd.name = nameBuf;
                pd.alive = (health > 0 && lifeState == 0);
                pd.isLocal = (pawn == localPawn);
                players.push_back(pd);
            }

            // Draw ESP for each player
            SelectObject(hdcMem, espFont);
            SetBkMode(hdcMem, TRANSPARENT);

            for (const auto& p : players) {
                if (!p.alive || p.isLocal) continue;

                // World to screen
                Vector3 screen = {};
                if (!WorldToScreen(p.position, screen, viewMatrix, gameW, gameH))
                    continue;

                // Calculate box height based on distance
                float dist = Distance3D(p.position, localPos);
                if (dist < 1.0f) continue;

                float boxHeight = std::clamp(18000.0f / dist, 20.0f, 400.0f);
                float boxWidth = boxHeight * 0.6f;

                int x = (int)screen.x;
                int y = (int)screen.y;
                int w = (int)boxWidth;
                int h = (int)boxHeight;

                // Team-based color
                COLORREF color = (p.team == 3) ? RGB(50, 200, 255)  // CT
                               : (p.team == 2) ? RGB(255, 200, 50)  // T
                               : RGB(255, 255, 255);

                // Only show enemies (or all if same team, just use different style)
                bool isEnemy = (p.team != localTeam);
                if (p.team == localTeam) continue; // skip teammates (optional)

                // Box ESP (2D rectangle)
                HPEN boxPen = CreatePen(PS_SOLID, 2, color);
                HPEN oldPen = (HPEN)SelectObject(hdcMem, boxPen);
                HBRUSH oldBr = (HBRUSH)SelectObject(hdcMem, GetStockObject(NULL_BRUSH));

                Rectangle(hdcMem, x - w / 2, y - h / 2, x + w / 2, y + h / 2);

                SelectObject(hdcMem, oldPen);
                SelectObject(hdcMem, oldBr);
                DeleteObject(boxPen);

                // Health bar
                int barW = 4;
                int barH = h;
                int barX = x - w / 2 - barW - 2;
                int barY = y - h / 2;
                int fillH = (int)(barH * std::clamp(p.health / 100.0f, 0.0f, 1.0f));

                HPEN barPen = CreatePen(PS_SOLID, 1, RGB(0, 0, 0));
                SelectObject(hdcMem, barPen);
                HBRUSH barBg = CreateSolidBrush(RGB(40, 40, 40));
                SelectObject(hdcMem, barBg);
                Rectangle(hdcMem, barX, barY, barX + barW, barY + barH);
                DeleteObject(barBg);

                COLORREF healthColor = (p.health > 70) ? RGB(0, 255, 0)
                                      : (p.health > 35) ? RGB(255, 255, 0)
                                      : RGB(255, 0, 0);
                HBRUSH barFill = CreateSolidBrush(healthColor);
                SelectObject(hdcMem, barFill);
                int fillTop = barY + barH - fillH;
                Rectangle(hdcMem, barX, fillTop, barX + barW, barY + barH);
                SelectObject(hdcMem, GetStockObject(NULL_BRUSH));
                SelectObject(hdcMem, oldPen);
                DeleteObject(barPen);
                DeleteObject(barFill);

                // Name
                SetTextColor(hdcMem, RGB(255, 255, 255));
                TextOutW(hdcMem, x - w / 2, y - h / 2 - 16, p.name.c_str(), (int)p.name.length());

                // Distance
                wchar_t distBuf[32];
                swprintf_s(distBuf, L"%.0fm", dist);
                SetTextColor(hdcMem, RGB(200, 200, 200));
                TextOutW(hdcMem, x - w / 2, y + h / 2 + 2, distBuf, (int)wcslen(distBuf));
            }

            // Draw radar
            SelectObject(hdcMem, radarLabelFont);
            DrawRadar(hdcMem, localPos, players);

            // Map name from GlobalVars (+0x188 is m_szMapName char[128])
            SelectObject(hdcMem, espFont);
            uintptr_t globalVars = Read<uintptr_t>(g_clientBase + Offsets::dwGlobalVars);
            if (globalVars) {
                char mapName[128] = {};
                ReadBuffer(globalVars + 0x188, mapName, sizeof(mapName));
                SetTextColor(hdcMem, RGB(0, 200, 255));
                TextOutA(hdcMem, 15, 10, mapName, (int)strlen(mapName));
            }

            // Info line
            int aliveCount = 0;
            for (const auto& p : players) if (p.alive) aliveCount++;
            wchar_t infoBuf[64];
            swprintf_s(infoBuf, L"Players: %d  |  Alive: %d", (int)players.size(), aliveCount);
            SetTextColor(hdcMem, RGB(150, 150, 150));
            TextOutW(hdcMem, 15, 28, infoBuf, (int)wcslen(infoBuf));
        }

        // Blit to screen
        BitBlt(hdcScreen, 0, 0, g_screenW, g_screenH, hdcMem, 0, 0, SRCCOPY);

        Sleep(16); // ~60fps
    }

    // Cleanup
    DeleteObject(espFont);
    DeleteObject(radarLabelFont);
    DeleteDC(hdcMem);
    DeleteObject(hbmMem);
    ReleaseDC(nullptr, hdcScreen);
    DestroyWindow(g_overlayWnd);
}

// ================== ENTRY ==================
int main() {
    // Find cs2.exe process
    PROCESSENTRY32W pe = {};
    pe.dwSize = sizeof(pe);
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    DWORD pid = 0;
    BOOL found = FALSE;

    if (snap != INVALID_HANDLE_VALUE) {
        if (Process32FirstW(snap, &pe)) {
            do {
                if (wcsstr(pe.szExeFile, L"cs2.exe") || wcsstr(pe.szExeFile, L"cs2")) {
                    pid = pe.th32ProcessID;
                    found = TRUE;
                    break;
                }
            } while (Process32NextW(snap, &pe));
        }
        CloseHandle(snap);
    }

    if (!found) {
        MessageBoxA(nullptr, "CS2 process not found!\nPlease start Counter-Strike 2 first.", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }

    // Open process
    g_process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, FALSE, pid);
    if (!g_process) {
        MessageBoxA(nullptr, "Failed to open CS2 process!\nTry running as Administrator.", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }

    // Get module bases
    g_clientBase = GetModuleBase(L"client.dll");
    g_engineBase = GetModuleBase(L"engine2.dll");

    if (!g_clientBase || !g_engineBase) {
        MessageBoxA(nullptr, "Failed to find game modules!\nMake sure CS2 is fully loaded.", "Error", MB_OK | MB_ICONERROR);
        CloseHandle(g_process);
        return 1;
    }

    // Run
    GameLoop();

    CloseHandle(g_process);
    return 0;
}
