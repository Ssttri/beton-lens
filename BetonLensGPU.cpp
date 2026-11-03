// BETON LENS · GPU — the constructivist lens moved onto the graphics card.
//
//   DXGI Desktop Duplication  ->  desktop frame stays in VRAM, delivered only when the desktop changes
//   pass 1 (cell-res PS)      ->  per-cell mean colour + luminance std-dev
//   pass 2 (full-res PS)      ->  content cells re-printed per pixel (3-tone pigment ramp, 4x4 Bayer);
//                                 flat cells replaced by analytic halftone geometry, weave, carved red
//   DirectComposition         ->  flip-model premultiplied swap chain on a click-through topmost window,
//                                 excluded from capture so the lens never sees itself
//
// Latency is bounded by composition: the lens shows desktop frame N in composition N+1 (one refresh),
// plus whatever wait is left until that refresh. It cannot be zero without living inside dwm.exe.
//
// Hotkeys: Ctrl+Alt+F12 on/off · Ctrl+Alt+F11 opacity 100/70/45% · Ctrl+Alt+F10 cell 8/12/18 px
//          Ctrl+Alt+Shift+F12 quit
// Flags:   --snapshot  write lens-gpu-capture.bmp and lens-gpu-frame.bmp after a few frames
//
// Build:   build-gpu.cmd  (MSVC + Windows SDK; no third-party code)

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdint>
#include <vector>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

using Microsoft::WRL::ComPtr;

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x11
#endif

// ---------------------------------------------------------------- shaders

static const char* kHlsl = R"(
cbuffer Params : register(b0) { int cs; int gw; int gh; float alpha; float thr; int stampX; int stampY; int stampS; };
Texture2D<float4> desk  : register(t0);
Texture2D<float4> cells : register(t1);
Texture2D<float4> stamp : register(t2);

struct VSO { float4 pos : SV_Position; };
VSO VS(uint id : SV_VertexID)
{
    VSO o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

static const float3 LUMW  = float3(0.2126, 0.7152, 0.0722);
static const float3 PAPER = float3(220, 216, 207) / 255.0;
static const float3 INK   = float3( 23,  24,  26) / 255.0;
static const float3 GREY  = float3(128, 124, 116) / 255.0;
static const float3 PIG[5] = {
    float3(128, 124, 116) / 255.0,   // neutral mid tone
    float3(163,  39,  28) / 255.0,   // rust red
    float3(176, 122,  28) / 255.0,   // ochre
    float3( 59, 106,  88) / 255.0,   // verdigris
    float3( 47,  84, 102) / 255.0 }; // slate
static const int BAYER[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

int pigment(float3 c)
{
    float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
    float s = mx <= 0 ? 0 : (mx - mn) / mx;
    if (!(s > 0.30 && mx > 0.20)) return 0;
    float d = mx - mn, h;
    if (mx == c.r)      h = 60 * fmod((c.g - c.b) / d + 6, 6);
    else if (mx == c.g) h = 60 * ((c.b - c.r) / d + 2);
    else                h = 60 * ((c.r - c.g) / d + 4);
    if (h < 38 || h >= 320) return 1;
    if (h < 72) return 2;
    if (h < 190) return 3;
    return 4;
}

float4 premul(float3 c) { return float4(c * alpha, alpha); }

// pass 1: one output texel per cell
float4 PSCells(VSO i) : SV_Target
{
    int2 c = int2(i.pos.xy);
    int2 o = c * cs;
    uint W, H; desk.GetDimensions(W, H);
    float3 s = 0; float sl = 0, sq = 0, n = 0;
    [loop] for (int y = 0; y < cs; y++)
    [loop] for (int x = 0; x < cs; x++)
    {
        int2 p = o + int2(x, y);
        if (p.x < (int)W && p.y < (int)H)
        {
            float3 col = desk.Load(int3(p, 0)).rgb;
            float l = dot(col, LUMW);
            s += col; sl += l; sq += l * l; n += 1;
        }
    }
    float ml = sl / n;
    return float4(s / n, sqrt(max(0, sq / n - ml * ml)));
}

// pass 2: full resolution
float4 PSMain(VSO i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    if (stampS > 0 && p.x >= stampX && p.y >= stampY && p.x < stampX + stampS && p.y < stampY + stampS)
        return premul(stamp.Load(int3(p - int2(stampX, stampY), 0)).rgb);

    int2 cell = p / cs;
    float4 st = cells.Load(int3(cell, 0));

    if (st.a * 255 > thr)
    {
        // content: three-tone print with ordered dither keeps text and icons legible
        float3 c = desk.Load(int3(p, 0)).rgb;
        float t = dot(c, LUMW) * 255 + (BAYER[(p.y & 3) * 4 + (p.x & 3)] - 8) * 5;
        return premul(t > 168 ? PAPER : t > 84 ? PIG[pigment(c)] : INK);
    }

    // field: constructivist geometry of the cell's mean tone
    float3 m = st.rgb;
    float dark = 1 - dot(m, LUMW);
    int id = pigment(m);
    bool chroma = id != 0;
    int lvl = dark < 0.10 ? (chroma ? 1 : 0) : dark < 0.28 ? 1 : dark < 0.45 ? 2 : dark < 0.62 ? 3 : dark < 0.80 ? 4 : 5;

    int2 lpI = p - cell * cs;
    float2 lp = float2(lpI) + 0.5;
    float half_ = cs * 0.5;
    bool gutter = lpI.x == cs - 1 || lpI.y == cs - 1;          // mortar
    bool weave = (((cell.x >> 2) + (cell.y >> 2)) & 1) == 0;   // 4x4 blocks alternate the diagonal
    float dS = abs(lp.x + lp.y - cs) / 1.4142, dB = abs(lp.x - lp.y) / 1.4142;
    float dd = weave ? dS : dB;

    float3 fg = chroma ? PIG[id] : INK, bg = PAPER;
    bool on;
    if (chroma && id == 1 && lvl >= 3) { bg = PIG[1]; fg = PAPER; on = !gutter && dd <= 0.10 * cs; }  // carved white on red
    else if (lvl == 0) on = false;
    else if (lvl == 1) on = !gutter && abs(lp.x - half_) <= cs * 0.17 && abs(lp.y - half_) <= cs * 0.17;
    else if (lvl == 5) { on = !gutter; bg = chroma ? fg : GREY; }
    else { float th = lvl == 2 ? 0.20 : lvl == 3 ? 0.36 : 0.56; on = !gutter && dd <= th * half_; }
    return premul(on ? fg : bg);
}
)";

// ---------------------------------------------------------------- state

struct Params { int cs, gw, gh; float alpha; float thr; int stampX, stampY, stampS; };

static HWND g_hwnd;
static bool g_on = true, g_quit = false, g_snapshot = false;
static int g_opacityIdx = 0, g_cellIdx = 1;
static const float kOpacity[] = { 1.0f, 0.70f, 0.45f };
static const int kCells[] = { 8, 12, 18 };
static wchar_t g_dir[MAX_PATH];

static void Log(const char* fmt, ...)
{
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%sBetonLensGPU.log", g_dir);
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"a") != 0 || !f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d ", t.wHour, t.wMinute, t.wSecond);
    va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
    fputc('\n', f);
    fclose(f);
}

#define CHECK(hr, what) do { HRESULT _h = (hr); if (FAILED(_h)) { Log("FAILED %s: 0x%08X", what, (unsigned)_h); return false; } } while (0)

struct Lens
{
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<IDXGIOutput1> output;
    ComPtr<IDXGIOutputDuplication> dup;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<IDCompositionDevice> dcomp;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual> visual;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> psCells, psMain;
    ComPtr<ID3D11Buffer> cb;
    ComPtr<ID3D11Texture2D> deskTex, cellTex, outTex, stampTex;
    ComPtr<ID3D11ShaderResourceView> deskSrv, cellSrv, stampSrv;
    ComPtr<ID3D11RenderTargetView> cellRtv, outRtv;
    RECT bounds{};
    int W = 0, H = 0, cs = 12, gw = 0, gh = 0;
    static const int kStamp = 76;

    bool Init()
    {
        // the adapter that drives the primary output owns the duplication
        ComPtr<IDXGIFactory1> factory;
        CHECK(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND && !output; a++)
        {
            ComPtr<IDXGIOutput> o;
            for (UINT i = 0; adapter->EnumOutputs(i, &o) != DXGI_ERROR_NOT_FOUND; i++)
            {
                DXGI_OUTPUT_DESC d; o->GetDesc(&d);
                if (d.AttachedToDesktop && d.DesktopCoordinates.left == 0 && d.DesktopCoordinates.top == 0)
                {
                    o.As(&output);
                    bounds = d.DesktopCoordinates;
                    DXGI_ADAPTER_DESC1 ad; adapter->GetDesc1(&ad);
                    char name[128]; WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof name, nullptr, nullptr);
                    Log("adapter: %s, output %ldx%ld", name, d.DesktopCoordinates.right - d.DesktopCoordinates.left,
                        d.DesktopCoordinates.bottom - d.DesktopCoordinates.top);
                    break;
                }
            }
            if (!output) adapter.Reset();
        }
        if (!output) { Log("no primary output found"); return false; }
        W = bounds.right - bounds.left; H = bounds.bottom - bounds.top;

        // Hybrid GPUs: pick the device by trial. A device on the wrong physical adapter makes
        // duplication answer E_INVALIDARG, and graphics routing can make "the output's adapter" lie.
        D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        ComPtr<IDXGIOutput5> out5; output.As(&out5);
        DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
        std::vector<ComPtr<IDXGIAdapter1>> candidates;
        candidates.push_back(adapter);
        ComPtr<IDXGIAdapter1> other;
        for (UINT a = 0; factory->EnumAdapters1(a, &other) != DXGI_ERROR_NOT_FOUND; a++) candidates.push_back(other);
        for (size_t c = 0; c < candidates.size() && !dup; c++)
        {
            ComPtr<ID3D11Device> d; ComPtr<ID3D11DeviceContext> x;
            HRESULT hr = D3D11CreateDevice(candidates[c].Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                           fl, 2, D3D11_SDK_VERSION, &d, nullptr, &x);
            DXGI_ADAPTER_DESC1 ad; candidates[c]->GetDesc1(&ad);
            char name[128]; WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof name, nullptr, nullptr);
            if (FAILED(hr)) { Log("  device on %s: 0x%08X", name, (unsigned)hr); continue; }
            HRESULT h1 = out5 ? out5->DuplicateOutput1(d.Get(), 0, 1, formats, &dup) : E_NOINTERFACE;
            HRESULT h0 = SUCCEEDED(h1) ? S_OK : output->DuplicateOutput(d.Get(), &dup);
            Log("  try %s: DuplicateOutput1 0x%08X, DuplicateOutput 0x%08X", name, (unsigned)h1, (unsigned)h0);
            if (dup) { dev = d; ctx = x; }
        }
        if (!dup)
        {
            // last resort: let the runtime choose the adapter
            ComPtr<ID3D11Device> d; ComPtr<ID3D11DeviceContext> x;
            if (SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                            fl, 2, D3D11_SDK_VERSION, &d, nullptr, &x)))
            {
                HRESULT h1 = out5 ? out5->DuplicateOutput1(d.Get(), 0, 1, formats, &dup) : E_NOINTERFACE;
                Log("  try default hardware device: 0x%08X", (unsigned)h1);
                if (dup) { dev = d; ctx = x; }
            }
        }
        if (!dup) { Log("no device can duplicate this output"); return false; }

        if (!Shaders()) return false;
        if (!Composition()) return false;
        if (!Textures()) return false;
        return Stamp();
    }

    bool Shaders()
    {
        auto compile = [&](const char* entry, const char* model, ComPtr<ID3DBlob>& blob) -> bool {
            ComPtr<ID3DBlob> err;
            HRESULT hr = D3DCompile(kHlsl, strlen(kHlsl), "lens.hlsl", nullptr, nullptr, entry, model,
                                    D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &blob, &err);
            if (FAILED(hr)) { Log("shader %s: %s", entry, err ? (const char*)err->GetBufferPointer() : "?"); return false; }
            return true;
        };
        ComPtr<ID3DBlob> b;
        if (!compile("VS", "vs_5_0", b)) return false;
        CHECK(dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vs), "VS");
        if (!compile("PSCells", "ps_5_0", b)) return false;
        CHECK(dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psCells), "PSCells");
        if (!compile("PSMain", "ps_5_0", b)) return false;
        CHECK(dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psMain), "PSMain");
        D3D11_BUFFER_DESC bd{ sizeof(Params) + (16 - sizeof(Params) % 16) % 16, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER };
        CHECK(dev->CreateBuffer(&bd, nullptr, &cb), "constant buffer");
        return true;
    }

    bool Composition()
    {
        ComPtr<IDXGIDevice> dxgiDev; dev.As(&dxgiDev);
        ComPtr<IDXGIAdapter> ad; dxgiDev->GetAdapter(&ad);
        ComPtr<IDXGIFactory2> f2; CHECK(ad->GetParent(IID_PPV_ARGS(&f2)), "IDXGIFactory2");
        DXGI_SWAP_CHAIN_DESC1 sd{};
        sd.Width = W; sd.Height = H; sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM; sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 2;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; sd.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        CHECK(f2->CreateSwapChainForComposition(dev.Get(), &sd, nullptr, &swap), "CreateSwapChainForComposition");
        CHECK(DCompositionCreateDevice(dxgiDev.Get(), IID_PPV_ARGS(&dcomp)), "DCompositionCreateDevice");
        CHECK(dcomp->CreateTargetForHwnd(g_hwnd, TRUE, &target), "CreateTargetForHwnd");
        CHECK(dcomp->CreateVisual(&visual), "CreateVisual");
        CHECK(visual->SetContent(swap.Get()), "SetContent");
        CHECK(target->SetRoot(visual.Get()), "SetRoot");
        CHECK(dcomp->Commit(), "Commit");
        return true;
    }

    bool Textures()
    {
        D3D11_TEXTURE2D_DESC td{};
        td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        CHECK(dev->CreateTexture2D(&td, nullptr, &deskTex), "deskTex");
        CHECK(dev->CreateShaderResourceView(deskTex.Get(), nullptr, &deskSrv), "deskSrv");
        td.BindFlags = D3D11_BIND_RENDER_TARGET;
        CHECK(dev->CreateTexture2D(&td, nullptr, &outTex), "outTex");
        CHECK(dev->CreateRenderTargetView(outTex.Get(), nullptr, &outRtv), "outRtv");
        return Cells(kCells[g_cellIdx]);
    }

    bool Cells(int size)
    {
        cs = size; gw = (W + cs - 1) / cs; gh = (H + cs - 1) / cs;
        cellTex.Reset(); cellSrv.Reset(); cellRtv.Reset();
        D3D11_TEXTURE2D_DESC td{};
        td.Width = gw; td.Height = gh; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        CHECK(dev->CreateTexture2D(&td, nullptr, &cellTex), "cellTex");
        CHECK(dev->CreateShaderResourceView(cellTex.Get(), nullptr, &cellSrv), "cellSrv");
        CHECK(dev->CreateRenderTargetView(cellTex.Get(), nullptr, &cellRtv), "cellRtv");
        return true;
    }

    /// The 构成 seal, drawn once with GDI and uploaded.
    bool Stamp()
    {
        const int S = kStamp;
        BITMAPINFO bi{}; bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = S; bi.bmiHeader.biHeight = -S; bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32;
        void* bits = nullptr;
        HDC dc = CreateCompatibleDC(nullptr);
        HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        HGDIOBJ old = SelectObject(dc, bmp);
        RECT all{ 0, 0, S, S };
        HBRUSH red = CreateSolidBrush(RGB(163, 39, 28)), paper = CreateSolidBrush(RGB(220, 216, 207));
        FillRect(dc, &all, red);
        RECT r1{ 5, 5, S - 5, 9 }, r2{ 5, S - 9, S - 5, S - 5 }, r3{ 5, 5, 9, S - 5 }, r4{ S - 9, 5, S - 5, S - 5 };
        FillRect(dc, &r1, paper); FillRect(dc, &r2, paper); FillRect(dc, &r3, paper); FillRect(dc, &r4, paper);
        RECT c1{ S - 30, 3, S - 18, 11 }, c2{ 3, S - 26, 11, S - 16 };   // chipped edges, as on a worn seal
        FillRect(dc, &c1, red); FillRect(dc, &c2, red);
        HFONT font = CreateFontW(-23, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, L"Noto Serif SC");
        HGDIOBJ oldFont = SelectObject(dc, font);
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(220, 216, 207));
        RECT t1{ 0, 9, S, S / 2 + 2 }, t2{ 0, S / 2 - 2, S, S - 9 };
        DrawTextW(dc, L"构", 1, &t1, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        DrawTextW(dc, L"成", 1, &t2, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        GdiFlush();
        D3D11_TEXTURE2D_DESC td{};
        td.Width = S; td.Height = S; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_IMMUTABLE; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA init{ bits, (UINT)(S * 4), 0 };
        HRESULT hr = dev->CreateTexture2D(&td, &init, &stampTex);
        SelectObject(dc, oldFont); DeleteObject(font);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); DeleteObject(red); DeleteObject(paper);
        CHECK(hr, "stampTex");
        CHECK(dev->CreateShaderResourceView(stampTex.Get(), nullptr, &stampSrv), "stampSrv");
        return true;
    }

    bool Duplicate()
    {
        dup.Reset();
        // Per-monitor-DPI-aware processes must use DuplicateOutput1; the old call answers UNSUPPORTED.
        ComPtr<IDXGIOutput5> out5;
        output.As(&out5);
        DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
        HRESULT hr = E_FAIL;
        for (int tries = 0; tries < 40; tries++)
        {
            hr = out5 ? out5->DuplicateOutput1(dev.Get(), 0, 1, formats, &dup) : E_NOINTERFACE;
            if (SUCCEEDED(hr)) return true;
            HRESULT hr0 = output->DuplicateOutput(dev.Get(), &dup);
            if (SUCCEEDED(hr0)) { Log("DuplicateOutput1 0x%08X, fell back to DuplicateOutput", (unsigned)hr); return true; }
            if (tries == 0)
            {
                DPI_AWARENESS_CONTEXT ctxDpi = GetThreadDpiAwarenessContext();
                Log("duplicate failed: DuplicateOutput1 0x%08X, DuplicateOutput 0x%08X, dpi-v2=%d", (unsigned)hr, (unsigned)hr0,
                    AreDpiAwarenessContextsEqual(ctxDpi, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) ? 1 : 0);
            }
            Sleep(250);   // secure desktop (UAC, lock screen) refuses duplication for a moment
        }
        Log("duplication kept failing: 0x%08X", (unsigned)hr);
        return false;
    }

    void Draw(float alpha)
    {
        Params p{ cs, gw, gh, alpha, 14.0f, W - kStamp - 28, H - kStamp - 84, kStamp };
        ctx->UpdateSubresource(cb.Get(), 0, nullptr, &p, 0, 0);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->IASetInputLayout(nullptr);
        ctx->VSSetShader(vs.Get(), nullptr, 0);
        ctx->PSSetConstantBuffers(0, 1, cb.GetAddressOf());

        // pass 1
        ID3D11ShaderResourceView* none[3] = {};
        ctx->PSSetShaderResources(0, 3, none);
        D3D11_VIEWPORT vp1{ 0, 0, (float)gw, (float)gh, 0, 1 };
        ctx->RSSetViewports(1, &vp1);
        ctx->OMSetRenderTargets(1, cellRtv.GetAddressOf(), nullptr);
        ctx->PSSetShader(psCells.Get(), nullptr, 0);
        ctx->PSSetShaderResources(0, 1, deskSrv.GetAddressOf());
        ctx->Draw(3, 0);

        // pass 2
        D3D11_VIEWPORT vp2{ 0, 0, (float)W, (float)H, 0, 1 };
        ctx->RSSetViewports(1, &vp2);
        ctx->OMSetRenderTargets(1, outRtv.GetAddressOf(), nullptr);
        ID3D11ShaderResourceView* srv[3] = { deskSrv.Get(), cellSrv.Get(), stampSrv.Get() };
        ctx->PSSetShader(psMain.Get(), nullptr, 0);
        ctx->PSSetShaderResources(0, 3, srv);
        ctx->Draw(3, 0);
        ctx->PSSetShaderResources(0, 3, none);

        ComPtr<ID3D11Texture2D> back;
        swap->GetBuffer(0, IID_PPV_ARGS(&back));
        ctx->CopyResource(back.Get(), outTex.Get());
        swap->Present(0, 0);
    }

    void Clear()
    {
        ComPtr<ID3D11Texture2D> back;
        swap->GetBuffer(0, IID_PPV_ARGS(&back));
        ComPtr<ID3D11RenderTargetView> rtv;
        dev->CreateRenderTargetView(back.Get(), nullptr, &rtv);
        float zero[4] = { 0, 0, 0, 0 };
        ctx->ClearRenderTargetView(rtv.Get(), zero);
        swap->Present(0, 0);
    }

    void SaveBmp(ID3D11Texture2D* tex, const wchar_t* name)
    {
        D3D11_TEXTURE2D_DESC td; tex->GetDesc(&td);
        td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ; td.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> st;
        if (FAILED(dev->CreateTexture2D(&td, nullptr, &st))) return;
        ctx->CopyResource(st.Get(), tex);
        D3D11_MAPPED_SUBRESOURCE m;
        if (FAILED(ctx->Map(st.Get(), 0, D3D11_MAP_READ, 0, &m))) return;
        wchar_t path[MAX_PATH]; swprintf_s(path, L"%s%s", g_dir, name);
        FILE* f = nullptr;
        if (_wfopen_s(&f, path, L"wb") == 0 && f)
        {
            BITMAPFILEHEADER fh{}; BITMAPINFOHEADER ih{};
            ih.biSize = sizeof ih; ih.biWidth = td.Width; ih.biHeight = -(LONG)td.Height; ih.biPlanes = 1; ih.biBitCount = 32;
            fh.bfType = 0x4D42; fh.bfOffBits = sizeof fh + sizeof ih; fh.bfSize = fh.bfOffBits + td.Width * td.Height * 4;
            fwrite(&fh, sizeof fh, 1, f); fwrite(&ih, sizeof ih, 1, f);
            for (UINT y = 0; y < td.Height; y++) fwrite((uint8_t*)m.pData + y * m.RowPitch, 4, td.Width, f);
            fclose(f);
        }
        ctx->Unmap(st.Get(), 0);
    }
};

// ---------------------------------------------------------------- window

enum { HK_TOGGLE = 1, HK_OPACITY, HK_CELL, HK_QUIT };

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_DESTROY) { g_quit = true; return 0; }
    return DefWindowProcW(h, msg, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, LPWSTR cmd, int)
{
    HANDLE single = CreateMutexW(nullptr, TRUE, L"BetonLensGPU.SingleInstance");
    if (GetLastError() == ERROR_ALREADY_EXISTS) return 0;
    BOOL dpiOk = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    GetModuleFileNameW(nullptr, g_dir, MAX_PATH);
    wchar_t* slash = wcsrchr(g_dir, L'\\'); if (slash) slash[1] = 0;
    if (!dpiOk) Log("SetProcessDpiAwarenessContext failed: %lu", GetLastError());
    g_snapshot = cmd && wcsstr(cmd, L"--snapshot");

    WNDCLASSW wc{}; wc.lpfnWndProc = WndProc; wc.hInstance = inst; wc.lpszClassName = L"BetonLensGPU";
    RegisterClassW(&wc);
    int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
    g_hwnd = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
                             WS_EX_TRANSPARENT | WS_EX_LAYERED,
                             wc.lpszClassName, L"BETON LENS GPU", WS_POPUP, 0, 0, sw, sh, nullptr, nullptr, inst, nullptr);
    SetLayeredWindowAttributes(g_hwnd, 0, 255, LWA_ALPHA);   // layered + transparent: every click passes through
    if (!SetWindowDisplayAffinity(g_hwnd, WDA_EXCLUDEFROMCAPTURE)) { Log("SetWindowDisplayAffinity failed"); return 1; }

    Lens lens;
    if (!lens.Init()) { Log("init failed"); return 1; }
    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    RegisterHotKey(g_hwnd, HK_TOGGLE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F12);
    RegisterHotKey(g_hwnd, HK_OPACITY, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F11);
    RegisterHotKey(g_hwnd, HK_CELL, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F10);
    RegisterHotKey(g_hwnd, HK_QUIT, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_F12);
    Log("start %dx%d", lens.W, lens.H);

    LARGE_INTEGER freq; QueryPerformanceFrequency(&freq);
    LARGE_INTEGER statStart; QueryPerformanceCounter(&statStart);
    int frames = 0, drawn = 0; double latencySum = 0, workSum = 0; int latencyN = 0;
    bool dirty = true;
    std::vector<uint8_t> meta;

    while (!g_quit)
    {
        MSG m;
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE))
        {
            if (m.message == WM_HOTKEY)
            {
                switch (m.wParam)
                {
                case HK_TOGGLE: g_on = !g_on; if (!g_on) lens.Clear(); dirty = true; break;
                case HK_OPACITY: g_opacityIdx = (g_opacityIdx + 1) % 3; dirty = true; break;
                case HK_CELL: g_cellIdx = (g_cellIdx + 1) % 3; lens.Cells(kCells[g_cellIdx]); dirty = true; break;
                case HK_QUIT: g_quit = true; break;
                }
            }
            TranslateMessage(&m); DispatchMessageW(&m);
        }
        if (g_quit) break;
        if (!g_on) { Sleep(50); continue; }

        DXGI_OUTDUPL_FRAME_INFO info; ComPtr<IDXGIResource> res;
        HRESULT hr = lens.dup->AcquireNextFrame(16, &info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            if (dirty) { lens.Draw(kOpacity[g_opacityIdx]); dirty = false; }
            continue;
        }
        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL)
        {
            Log("duplication lost (0x%08X), recreating", (unsigned)hr);
            if (!lens.Duplicate()) break;
            dirty = true;
            continue;
        }
        if (FAILED(hr)) { Log("AcquireNextFrame 0x%08X", (unsigned)hr); Sleep(100); continue; }

        LARGE_INTEGER t0; QueryPerformanceCounter(&t0);
        bool changed = info.LastPresentTime.QuadPart != 0;
        if (changed && info.TotalMetadataBufferSize > 0)
        {
            // our own presents also wake duplication; skip frames whose dirty region is empty
            meta.resize(info.TotalMetadataBufferSize);
            UINT need = 0;
            if (SUCCEEDED(lens.dup->GetFrameDirtyRects((UINT)meta.size(), (RECT*)meta.data(), &need)))
                changed = need > 0;
        }
        if (changed || dirty)
        {
            ComPtr<ID3D11Texture2D> src; res.As(&src);
            lens.ctx->CopyResource(lens.deskTex.Get(), src.Get());
        }
        lens.dup->ReleaseFrame();
        if (!(changed || dirty)) continue;

        lens.Draw(kOpacity[g_opacityIdx]);
        dirty = false;
        frames++; drawn++;
        LARGE_INTEGER t1; QueryPerformanceCounter(&t1);
        workSum += double(t1.QuadPart - t0.QuadPart) * 1000.0 / freq.QuadPart;
        if (info.LastPresentTime.QuadPart)
        {
            latencySum += double(t1.QuadPart - info.LastPresentTime.QuadPart) * 1000.0 / freq.QuadPart;
            latencyN++;
        }

        if (g_snapshot && frames == 10)
        {
            lens.SaveBmp(lens.deskTex.Get(), L"lens-gpu-capture.bmp");
            lens.SaveBmp(lens.outTex.Get(), L"lens-gpu-frame.bmp");
            Log("snapshot saved");
        }
        if (frames % 60 == 0)
            SetWindowPos(g_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        double span = double(t1.QuadPart - statStart.QuadPart) / freq.QuadPart;
        if (span >= 5.0)
        {
            Log("%.1f fps drawn, CPU submit %.2f ms/frame, desktop-present -> lens-present %.2f ms",
                drawn / span, drawn ? workSum / drawn : 0.0, latencyN ? latencySum / latencyN : 0.0);
            statStart = t1; drawn = 0; workSum = 0; latencySum = 0; latencyN = 0;
        }
    }

    lens.Clear();
    DestroyWindow(g_hwnd);
    if (single) CloseHandle(single);
    Log("quit");
    return 0;
}
