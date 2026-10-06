#include "smtc_writer.h"

#include <windows.h>
#include <propsys.h>

#include <string>
#include <vector>

#include "logger.h"

// 通过 Windows 自带的属性系统读取标签，不依赖任何第三方库。
// 好处：MP3(ID3v2) / FLAC(Vorbis) / M4A / WMA 都由系统解码器覆盖，
// 而且 PROPERTYKEY 用规范名在运行时解析，不需要硬编码 GUID。
extern "C" HRESULT WINAPI SHGetPropertyStoreFromParsingName(PCWSTR pszPath, IBindCtx* pbc,
                                                            GETPROPERTYSTOREFLAGS flags,
                                                            REFIID riid, void** ppv);

namespace {

// ---------------------------------------------------------------------------
// 接口 IID —— 全部由运行时 GetIids 实测输出（见日志 [smtc] / [updater] / [musicprops]）
// ---------------------------------------------------------------------------
const GUID kIidMusicDisplayProperties = {
    0x6BBF0C59, 0xD0A0, 0x4D26, {0x92, 0xA0, 0xF9, 0x78, 0xE1, 0xD1, 0x8E, 0x7B}};
const GUID kIidMusicDisplayProperties2 = {
    0x00368462, 0x97D3, 0x44B9, {0xB0, 0x0F, 0x00, 0x8A, 0xFC, 0xEF, 0xAF, 0x18}};
const GUID kIidMusicDisplayProperties3 = {
    0x4DB51AC1, 0x0681, 0x4E8C, {0x94, 0x01, 0xB8, 0x15, 0x9D, 0x9E, 0xEF, 0xC7}};

// ---------------------------------------------------------------------------
// vtable 下标 = 6 + 接口自身第 N 个方法（IInspectable 占前 6 槽）
// 依据 docs/05-smtc-abi.md，全部由 Windows.Media.winmd 导出并经运行验证
// ---------------------------------------------------------------------------
constexpr int kSlotGetDisplayUpdater = 8;    // ISystemMediaTransportControls
constexpr int kSlotGetMusicProperties = 12;  // DisplayUpdater
constexpr int kSlotUpdaterUpdate = 17;       // DisplayUpdater::Update

constexpr int kSlotPutTitle = 7;             // IMusicDisplayProperties
constexpr int kSlotPutAlbumArtist = 9;
constexpr int kSlotPutArtist = 11;

constexpr int kSlotPutAlbumTitle = 7;        // IMusicDisplayProperties2
constexpr int kSlotPutTrackNumber = 9;
constexpr int kSlotGetGenres = 10;

// Windows.Foundation.Collections.IVector`1 自身方法顺序（winmd 导出）：
//   GetAt, get_Size, GetView, IndexOf, SetAt, InsertAt, RemoveAt, Append,
//   RemoveAtEnd, Clear, GetMany, ReplaceAll
// Append 是第 7 个 → 槽位 6+7=13；Clear 是第 9 个 → 槽位 15
constexpr int kSlotVectorAppend = 13;
constexpr int kSlotVectorClear = 15;

// ---------------------------------------------------------------------------

typedef HRESULT(WINAPI* PFN_QueryInterface)(void* self, REFIID iid, void** out);
typedef HRESULT(WINAPI* PFN_GetObject)(void* self, void** out);
typedef HRESULT(WINAPI* PFN_Update)(void* self);
typedef HRESULT(WINAPI* PFN_PutString)(void* self, void* hstring);
typedef HRESULT(WINAPI* PFN_PutInt)(void* self, int value);
typedef HRESULT(WINAPI* PFN_VectorOp)(void* self, void* value);
typedef HRESULT(WINAPI* PFN_VectorClear)(void* self);
typedef HRESULT(WINAPI* PFN_WindowsCreateString)(LPCWSTR src, UINT32 len, void** hstring);
typedef HRESULT(WINAPI* PFN_WindowsDeleteString)(void* hstring);
typedef HRESULT(WINAPI* PFN_PSGetPropertyKeyFromName)(PCWSTR name, PROPERTYKEY* key);

PFN_WindowsCreateString g_createString = nullptr;
PFN_WindowsDeleteString g_deleteString = nullptr;
PFN_PSGetPropertyKeyFromName g_propertyKeyFromName = nullptr;

void* g_musicV1 = nullptr;
void* g_musicV2 = nullptr;
void* g_musicV3 = nullptr;
PFN_Update g_realUpdate = nullptr;
bool g_updateHooked = false;

CRITICAL_SECTION g_lock;
bool g_ready = false;

wchar_t g_currentFile[MAX_PATH] = {};  // 由 CreateFileW 探针写入

// ---------------------------------------------------------------------------
// 配置：与 DLL 同目录的 PotPlayerSmtcHook.ini
// 由前端界面写入，这里只读。
// ---------------------------------------------------------------------------
bool g_tagFirst = true;
std::wstring g_namePattern = L"%artist% - %title%";
std::wstring g_configPath;

void LoadConfig() {
    if (g_configPath.empty()) return;
    const wchar_t* path = g_configPath.c_str();

    g_tagFirst = GetPrivateProfileIntW(L"meta", L"tag_first", 1, path) != 0;

    wchar_t buf[512] = {};
    GetPrivateProfileStringW(L"meta", L"name_pattern", L"%artist% - %title%", buf, 512, path);
    g_namePattern = buf;
    if (g_namePattern.empty()) g_namePattern = L"%artist% - %title%";
}

struct TrackMeta {
    std::wstring title;
    std::wstring artist;
    std::wstring albumArtist;
    std::wstring album;
    std::wstring genre;
    int trackNumber = 0;
    bool fromTags = false;
};

TrackMeta g_cachedMeta;          // 由标签线程填充
std::wstring g_cachedPath;       // 上面这份对应哪个文件

void** VtableOf(void* object) { return *reinterpret_cast<void***>(object); }

void* Qi(void* object, const GUID& iid) {
    if (!object) return nullptr;
    void* out = nullptr;
    const HRESULT hr =
        reinterpret_cast<PFN_QueryInterface>(VtableOf(object)[0])(object, iid, &out);
    return SUCCEEDED(hr) ? out : nullptr;
}

void PutString(void* iface, int slot, const wchar_t* text) {
    if (!iface) return;
    // 空值必须清掉，否则切到「没有这个标签」的歌时，上一首的值会留在 SMTC 里。
    // 实测：传空串宿主会忽略（专辑照样是上一首的）；传 NULL 才能真正清除。
    if (!text || !*text) {
        reinterpret_cast<PFN_PutString>(VtableOf(iface)[slot])(iface, nullptr);
        return;
    }
    if (!g_createString) return;
    void* hstring = nullptr;
    const size_t len = wcslen(text);
    if (FAILED(g_createString(text, static_cast<UINT32>(len), &hstring)) || !hstring) return;
    reinterpret_cast<PFN_PutString>(VtableOf(iface)[slot])(iface, hstring);
    if (g_deleteString) g_deleteString(hstring);
}

// ---------------------------------------------------------------------------
// 文件名解析：用户可编辑的「伪正则」
//
// 语法：%字段% 为占位符，其余字符按字面量匹配；%% 表示字面量 %。
// 支持字段：%title% %artist% %album% %albumArtist% %track%（大小写不敏感）
//
// 匹配规则（顺序查找，不做回溯）：
//   * 字面量在剩余文本中查找第一次出现的位置；
//   * 它之前的文本归给「上一个占位符」；
//   * 末尾仍处于挂起状态的占位符吃掉剩下的全部文本。
//
// 例：%artist% - %title%   对 "周杰伦 - 夜曲" → artist=周杰伦, title=夜曲
//     [%artist%] %title%   对 "[周杰伦] 夜曲" → artist=周杰伦, title=夜曲
//     %title%              对 "任意名字"     → title=任意名字
// ---------------------------------------------------------------------------
struct NameParts {
    std::wstring stem;
    std::wstring title;
    std::wstring artist;
    std::wstring album;
    std::wstring albumArtist;
    int track = 0;
};

std::wstring ToLower(const std::wstring& text) {
    std::wstring out = text;
    for (auto& ch : out) ch = static_cast<wchar_t>(towlower(ch));
    return out;
}

void TrimInPlace(std::wstring* text) {
    while (!text->empty() && (text->front() == L' ' || text->front() == L'\t')) {
        text->erase(text->begin());
    }
    while (!text->empty() && (text->back() == L' ' || text->back() == L'\t')) {
        text->pop_back();
    }
}

std::wstring StemOf(const std::wstring& path) {
    std::wstring name = path;
    const size_t slash = name.find_last_of(L"\\/");
    if (slash != std::wstring::npos) name = name.substr(slash + 1);
    const size_t dot = name.find_last_of(L'.');
    if (dot != std::wstring::npos && dot > 0) name = name.substr(0, dot);
    return name;
}

NameParts MatchPattern(const std::wstring& text, const std::wstring& pattern) {
    // 先把 pattern 拆成「字面量 / 占位符」序列
    std::vector<std::pair<bool, std::wstring>> elements;  // true = 占位符
    std::wstring literal;
    for (size_t i = 0; i < pattern.size(); ++i) {
        if (pattern[i] != L'%') {
            literal += pattern[i];
            continue;
        }
        const size_t end = pattern.find(L'%', i + 1);
        if (end == std::wstring::npos) {
            literal += pattern.substr(i);
            break;
        }
        const std::wstring field = pattern.substr(i + 1, end - i - 1);
        if (field.empty()) {
            literal += L'%';  // %% → 字面量
        } else {
            if (!literal.empty()) {
                elements.emplace_back(false, literal);
                literal.clear();
            }
            elements.emplace_back(true, field);
        }
        i = end;
    }
    if (!literal.empty()) elements.emplace_back(false, literal);

    NameParts parts;
    parts.stem = text;
    if (elements.empty()) {
        parts.title = text;
        return parts;
    }

    auto assign = [&parts](const std::wstring& field, std::wstring value) {
        TrimInPlace(&value);
        const std::wstring name = ToLower(field);
        if (name == L"title") {
            parts.title = value;
        } else if (name == L"artist") {
            parts.artist = value;
        } else if (name == L"album") {
            parts.album = value;
        } else if (name == L"albumartist") {
            parts.albumArtist = value;
        } else if (name == L"track") {
            parts.track = _wtoi(value.c_str());
        }
    };

    const std::wstring lower = ToLower(text);
    size_t pos = 0;
    std::wstring pending;
    bool failed = false;

    for (const auto& element : elements) {
        if (element.first) {
            if (!pending.empty()) assign(pending, L"");  // 连续两个占位符，前者为空
            pending = element.second;
            continue;
        }
        if (element.second.empty()) continue;
        const size_t idx = lower.find(ToLower(element.second), pos);
        if (idx == std::wstring::npos) {
            failed = true;
            break;
        }
        if (!pending.empty()) {
            assign(pending, text.substr(pos, idx - pos));
            pending.clear();
        }
        pos = idx + element.second.size();
    }
    if (!failed && !pending.empty()) assign(pending, text.substr(pos));

    const bool capturedAnything = !parts.title.empty() || !parts.artist.empty() ||
                                  !parts.album.empty() || !parts.albumArtist.empty() ||
                                  parts.track > 0;
    if (failed || !capturedAnything) {
        NameParts fallback;
        fallback.stem = text;
        fallback.title = text;
        return fallback;
    }
    return parts;
}

NameParts ParseName(const std::wstring& path) {
    if (path.empty()) return NameParts{};
    return MatchPattern(StemOf(path), g_namePattern);
}

// ---------------------------------------------------------------------------
// 用系统属性系统读标签
// ---------------------------------------------------------------------------
std::wstring PropToString(const PROPVARIANT& pv) {
    switch (pv.vt) {
        case VT_LPWSTR:
            return pv.pwszVal ? pv.pwszVal : L"";
        case VT_BSTR:
            return pv.bstrVal ? pv.bstrVal : L"";
        case VT_LPSTR:
            if (!pv.pszVal) return L"";
            {
                const int need = MultiByteToWideChar(CP_ACP, 0, pv.pszVal, -1, nullptr, 0);
                if (need <= 1) return L"";
                std::wstring out(static_cast<size_t>(need - 1), L'\0');
                MultiByteToWideChar(CP_ACP, 0, pv.pszVal, -1, &out[0], need);
                return out;
            }
        case VT_VECTOR | VT_LPWSTR: {
            // 多值字段（例如 Artist）用 "; " 连接
            std::wstring joined;
            const auto& vec = pv.calpwstr;
            for (ULONG i = 0; i < vec.cElems; ++i) {
                if (!vec.pElems[i]) continue;
                if (!joined.empty()) joined += L"; ";
                joined += vec.pElems[i];
            }
            return joined;
        }
        default:
            return L"";
    }
}

bool ReadProp(IPropertyStore* store, const PROPERTYKEY& key, PROPVARIANT* out) {
    PropVariantInit(out);
    return SUCCEEDED(store->GetValue(key, out)) && out->vt != VT_EMPTY;
}

bool ResolveKey(const wchar_t* canonicalName, PROPERTYKEY* key) {
    return g_propertyKeyFromName && SUCCEEDED(g_propertyKeyFromName(canonicalName, key));
}

TrackMeta ReadTags(const std::wstring& path) {
    TrackMeta meta;
    if (!g_propertyKeyFromName) return meta;

    IPropertyStore* store = nullptr;
    const HRESULT hr = SHGetPropertyStoreFromParsingName(path.c_str(), nullptr, GPS_DEFAULT,
                                                         IID_IPropertyStore, (void**)&store);
    if (FAILED(hr) || !store) return meta;

    PROPVARIANT pv;
    PROPERTYKEY key;

    if (ResolveKey(L"System.Title", &key) && ReadProp(store, key, &pv)) {
        meta.title = PropToString(pv);
        PropVariantClear(&pv);
    }
    if (ResolveKey(L"System.Music.Artist", &key) && ReadProp(store, key, &pv)) {
        meta.artist = PropToString(pv);
        PropVariantClear(&pv);
    }
    if (ResolveKey(L"System.Music.AlbumArtist", &key) && ReadProp(store, key, &pv)) {
        meta.albumArtist = PropToString(pv);
        PropVariantClear(&pv);
    }
    if (ResolveKey(L"System.Music.AlbumTitle", &key) && ReadProp(store, key, &pv)) {
        meta.album = PropToString(pv);
        PropVariantClear(&pv);
    }
    if (ResolveKey(L"System.Music.Genre", &key) && ReadProp(store, key, &pv)) {
        meta.genre = PropToString(pv);
        PropVariantClear(&pv);
    }
    if (ResolveKey(L"System.Music.TrackNumber", &key) && ReadProp(store, key, &pv)) {
        if (pv.vt == VT_UI4) meta.trackNumber = static_cast<int>(pv.ulVal);
        PropVariantClear(&pv);
    }

    store->Release();
    meta.fromTags = !meta.title.empty() || !meta.artist.empty() || !meta.album.empty();
    return meta;
}

// ---------------------------------------------------------------------------
// 合并规则：标签优先，文件名兜底
//
//   Title       ← 标签 Title      > 文件名 " - " 后半段 > 文件名整体
//   Artist      ← 标签 Artist     > 文件名 " - " 前半段 > 留空
//   AlbumArtist ← 标签 AlbumArtist > 标签 Artist > 文件名前半段 > 留空
//   Album       ← 标签 Album      > 留空
//   TrackNumber / Genres ← 标签
// ---------------------------------------------------------------------------
TrackMeta BuildMeta(const std::wstring& path) {
    const NameParts name = ParseName(path);
    const TrackMeta tags = ReadTags(path);

    TrackMeta meta;
    meta.fromTags = tags.fromTags;

    if (g_tagFirst) {
        // 标签优先，文件名兜底
        meta.title = !tags.title.empty() ? tags.title : name.title;
        meta.artist = !tags.artist.empty() ? tags.artist : name.artist;
        if (!tags.albumArtist.empty()) {
            meta.albumArtist = tags.albumArtist;
        } else if (!tags.artist.empty()) {
            meta.albumArtist = tags.artist;
        } else {
            meta.albumArtist = name.albumArtist.empty() ? name.artist : name.albumArtist;
        }
        meta.album = !tags.album.empty() ? tags.album : name.album;
        meta.genre = tags.genre;
        meta.trackNumber = tags.trackNumber > 0 ? tags.trackNumber : name.track;
    } else {
        // 只用文件名
        meta.title = !name.title.empty() ? name.title : tags.title;
        meta.artist = name.artist;
        meta.albumArtist = name.albumArtist.empty() ? name.artist : name.albumArtist;
        meta.album = name.album;
        meta.genre.clear();
        meta.trackNumber = name.track;
    }
    return meta;
}

// 标签读取涉及磁盘 I/O，放到独立线程；探针线程只负责更新路径。
DWORD WINAPI TagReaderThread(LPVOID) {
    std::wstring lastPath;
    for (;;) {
        std::wstring path;
        EnterCriticalSection(&g_lock);
        path = g_currentFile;
        LeaveCriticalSection(&g_lock);

        if (!path.empty() && path != lastPath) {
            lastPath = path;
            const TrackMeta meta = BuildMeta(path);
            EnterCriticalSection(&g_lock);
            if (g_currentFile == path) {
                g_cachedPath = path;
                g_cachedMeta = meta;
            }
            LeaveCriticalSection(&g_lock);
            LogF("[tags] %s: title=%s artist=%s album=%s track=%d genres=%s",
                 meta.fromTags ? "读到了标签" : "无标签，用文件名", WideToUtf8(meta.title.c_str()).c_str(),
                 WideToUtf8(meta.artist.c_str()).c_str(), WideToUtf8(meta.album.c_str()).c_str(),
                 meta.trackNumber, WideToUtf8(meta.genre.c_str()).c_str());
        }
        Sleep(250);
    }
    return 0;
}

// ---------------------------------------------------------------------------

void WriteGenres(void* musicV2, const std::wstring& genre) {
    if (!musicV2 || !g_createString) return;

    void* vector = nullptr;
    if (FAILED(reinterpret_cast<PFN_GetObject>(VtableOf(musicV2)[kSlotGetGenres])(musicV2,
                                                                                 &vector)) ||
        !vector) {
        LogF("[writer] get_Genres 失败，跳过流派");
        return;
    }

    // 先清空：既避免多次 Update 累积重复项，也保证「新歌没有流派」时
    // 不会把上一首的流派留在 SMTC 里（实测过这个残留）。
    reinterpret_cast<PFN_VectorClear>(VtableOf(vector)[kSlotVectorClear])(vector);
    if (genre.empty()) return;

    // 支持 "A;B" / "A;B" 这类多流派写法
    size_t start = 0;
    while (start <= genre.size()) {
        size_t sep = genre.find_first_of(L";/", start);
        std::wstring one = genre.substr(start, sep == std::wstring::npos ? std::wstring::npos
                                                                         : sep - start);
        while (!one.empty() && one.front() == L' ') one.erase(one.begin());
        while (!one.empty() && one.back() == L' ') one.pop_back();
        if (!one.empty()) {
            void* hstring = nullptr;
            if (SUCCEEDED(g_createString(one.c_str(), static_cast<UINT32>(one.size()), &hstring)) &&
                hstring) {
                reinterpret_cast<PFN_VectorOp>(VtableOf(vector)[kSlotVectorAppend])(vector,
                                                                                   hstring);
                if (g_deleteString) g_deleteString(hstring);
            }
        }
        if (sep == std::wstring::npos) break;
        start = sep + 1;
    }
}

// 把我们的值写进 MusicProperties。必须在宿主调用 Update() 之前写入，
// 这样提交出去的就是我们的值。
void ApplyMetadata() {
    if (!g_musicV1 || !g_ready) return;

    std::wstring path;
    TrackMeta meta;
    bool haveCache = false;
    EnterCriticalSection(&g_lock);
    path = g_currentFile;
    if (!g_cachedPath.empty() && g_cachedPath == path) {
        meta = g_cachedMeta;
        haveCache = true;
    }
    LeaveCriticalSection(&g_lock);
    if (path.empty()) return;

    // 标签还没读回来时，先用文件名顶一下，保证切歌瞬间也不空
    if (!haveCache) meta = BuildMeta(path);
    if (meta.title.empty()) return;

    PutString(g_musicV1, kSlotPutTitle, meta.title.c_str());
    PutString(g_musicV1, kSlotPutArtist, meta.artist.c_str());
    PutString(g_musicV1, kSlotPutAlbumArtist,
              meta.albumArtist.empty() ? meta.artist.c_str() : meta.albumArtist.c_str());
    PutString(g_musicV2, kSlotPutAlbumTitle, meta.album.c_str());
    // 没有曲目号时写 0，同样是为了清掉上一首的残留
    reinterpret_cast<PFN_PutInt>(VtableOf(g_musicV2)[kSlotPutTrackNumber])(g_musicV2,
                                                                           meta.trackNumber);
    WriteGenres(g_musicV2, meta.genre);
}

HRESULT WINAPI Hook_Update(void* self) {
    ApplyMetadata();
    return g_realUpdate(self);
}

}  // namespace

void SmtcWriter_Init() {
    if (g_ready) return;
    InitializeCriticalSection(&g_lock);
    g_ready = true;

    // 配置文件与 DLL 同目录，由前端界面写入
    {
        HMODULE self = nullptr;
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                   GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&SmtcWriter_Init), &self)) {
            wchar_t path[MAX_PATH] = {};
            GetModuleFileNameW(self, path, MAX_PATH);
            const std::wstring full(path);
            const size_t pos = full.find_last_of(L"\\/");
            if (pos != std::wstring::npos) {
                g_configPath = full.substr(0, pos + 1) + L"PotPlayerSmtcHook.ini";
            }
        }
        LoadConfig();
        LogF("[writer] 配置: tag_first=%d pattern=%s", g_tagFirst ? 1 : 0,
             WideToUtf8(g_namePattern.c_str()).c_str());
    }


    HMODULE combase = GetModuleHandleW(L"combase.dll");
    if (!combase) combase = LoadLibraryW(L"combase.dll");
    if (combase) {
        g_createString = reinterpret_cast<PFN_WindowsCreateString>(
            GetProcAddress(combase, "WindowsCreateString"));
        g_deleteString = reinterpret_cast<PFN_WindowsDeleteString>(
            GetProcAddress(combase, "WindowsDeleteString"));
    }

    HMODULE propsys = GetModuleHandleW(L"propsys.dll");
    if (!propsys) propsys = LoadLibraryW(L"propsys.dll");
    if (propsys) {
        g_propertyKeyFromName = reinterpret_cast<PFN_PSGetPropertyKeyFromName>(
            GetProcAddress(propsys, "PSGetPropertyKeyFromName"));
    }

    LogF("[writer] 就绪：WindowsCreateString=%p PSGetPropertyKeyFromName=%p",
         reinterpret_cast<void*>(g_createString),
         reinterpret_cast<void*>(g_propertyKeyFromName));
}

void SmtcWriter_ReloadConfig() {
    if (!g_ready) return;
    LoadConfig();
    // 作废缓存，新规则下一个文件立即生效
    EnterCriticalSection(&g_lock);
    g_cachedPath.clear();
    g_cachedMeta = TrackMeta{};
    LeaveCriticalSection(&g_lock);
    LogF("[writer] 配置已重载: tag_first=%d pattern=%s", g_tagFirst ? 1 : 0,
         WideToUtf8(g_namePattern.c_str()).c_str());
}

void SmtcWriter_SetCurrentFile(const wchar_t* path) {
    if (!path || !g_ready) return;
    EnterCriticalSection(&g_lock);
    const bool changed = (g_currentFile[0] == 0) || (wcscmp(g_currentFile, path) != 0);
    wcsncpy(g_currentFile, path, MAX_PATH - 1);
    g_currentFile[MAX_PATH - 1] = 0;
    LeaveCriticalSection(&g_lock);
    (void)changed;
}

void SmtcWriter_Attach(void* smtcObject) {
    if (!smtcObject) return;
    if (!g_ready) SmtcWriter_Init();

    void* updater = nullptr;
    const HRESULT hrUpdater =
        reinterpret_cast<PFN_GetObject>(VtableOf(smtcObject)[kSlotGetDisplayUpdater])(
            smtcObject, &updater);
    if (FAILED(hrUpdater) || !updater) {
        LogF("[writer] get_DisplayUpdater 失败 hr=0x%08lX", static_cast<unsigned long>(hrUpdater));
        return;
    }

    void* music = nullptr;
    const HRESULT hrMusic =
        reinterpret_cast<PFN_GetObject>(VtableOf(updater)[kSlotGetMusicProperties])(updater,
                                                                                   &music);
    if (FAILED(hrMusic) || !music) {
        LogF("[writer] get_MusicProperties 失败 hr=0x%08lX", static_cast<unsigned long>(hrMusic));
        return;
    }

    g_musicV1 = Qi(music, kIidMusicDisplayProperties);
    g_musicV2 = Qi(music, kIidMusicDisplayProperties2);
    g_musicV3 = Qi(music, kIidMusicDisplayProperties3);
    LogF("[writer] MusicProperties v1=%p v2=%p v3=%p", g_musicV1, g_musicV2, g_musicV3);
    if (!g_musicV1) {
        LogF("[writer] 拿不到 IMusicDisplayProperties，放弃");
        return;
    }

    void** updaterVtable = VtableOf(updater);
    DWORD oldProtect = 0;
    if (!g_updateHooked &&
        VirtualProtect(&updaterVtable[kSlotUpdaterUpdate], sizeof(void*), PAGE_READWRITE,
                       &oldProtect)) {
        g_realUpdate = reinterpret_cast<PFN_Update>(updaterVtable[kSlotUpdaterUpdate]);
        updaterVtable[kSlotUpdaterUpdate] = reinterpret_cast<void*>(&Hook_Update);
        VirtualProtect(&updaterVtable[kSlotUpdaterUpdate], sizeof(void*), oldProtect, &oldProtect);
        g_updateHooked = true;
        LogF("[writer] 已挂钩 DisplayUpdater::Update (vtable[%d])", kSlotUpdaterUpdate);
    }

    HANDLE tagThread = CreateThread(nullptr, 0, TagReaderThread, nullptr, 0, nullptr);
    if (tagThread) CloseHandle(tagThread);

    ApplyMetadata();
    if (g_realUpdate) g_realUpdate(updater);
    LogF("[writer] 首次写入完成");
}
