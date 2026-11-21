#include <windows.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <atomic>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Shell32.lib")

namespace
{
    constexpr CLSID CommandClassId = { 0xb6b843e1, 0x1d33, 0x4e31, { 0x91, 0xa0, 0x7f, 0x91, 0x5a, 0x7e, 0x95, 0x72 } };
    constexpr wchar_t AppPathRegistryKey[] = L"Software\\Classes\\CLSID\\{B6B843E1-1D33-4E31-91A0-7F915A7E9572}";
    constexpr char CommandVerbA[] = "albumshell.addtoalbum";
    constexpr wchar_t CommandVerbW[] = L"albumshell.addtoalbum";
    constexpr wchar_t MenuTitle[] = L"Add to OneDrive album";
    constexpr wchar_t HelpText[] = L"Add the selected images to a OneDrive album.";
    std::atomic<ULONG> ActiveObjects = 0;
    std::atomic<ULONG> ServerLocks = 0;

    HRESULT ReadApplicationPath(std::wstring& path)
    {
        DWORD size = 0;
        LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, AppPathRegistryKey, L"AppPath", RRF_RT_REG_SZ, nullptr, nullptr, &size);
        if (status != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(status);

        std::vector<wchar_t> buffer(size / sizeof(wchar_t) + 1);
        status = RegGetValueW(HKEY_CURRENT_USER, AppPathRegistryKey, L"AppPath", RRF_RT_REG_SZ, nullptr, buffer.data(), &size);
        if (status != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(status);

        path.assign(buffer.data());
        return path.empty() ? HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) : S_OK;
    }

    std::wstring QuoteArgument(const std::wstring& value)
    {
        std::wstring quoted;
        quoted.push_back(L'"');
        size_t backslashes = 0;
        for (wchar_t character : value)
        {
            if (character == L'\\')
            {
                ++backslashes;
            }
            else if (character == L'"')
            {
                quoted.append(backslashes * 2 + 1, L'\\');
                quoted.push_back(character);
                backslashes = 0;
            }
            else
            {
                quoted.append(backslashes, L'\\');
                quoted.push_back(character);
                backslashes = 0;
            }
        }
        quoted.append(backslashes * 2, L'\\');
        quoted.push_back(L'"');
        return quoted;
    }

    HRESULT LaunchApplication(const std::vector<std::wstring>& paths)
    {
        std::wstring applicationPath;
        HRESULT result = ReadApplicationPath(applicationPath);
        if (FAILED(result))
            return result;

        std::wstring commandLine = QuoteArgument(applicationPath);
        for (const auto& path : paths)
        {
            commandLine.push_back(L' ');
            commandLine += QuoteArgument(path);
        }

        STARTUPINFOW startupInfo{};
        startupInfo.cb = sizeof(startupInfo);
        PROCESS_INFORMATION processInfo{};
        if (!CreateProcessW(applicationPath.c_str(), commandLine.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startupInfo, &processInfo))
            return HRESULT_FROM_WIN32(GetLastError());

        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        return S_OK;
    }

    class AlbumContextMenu final : public IShellExtInit, public IContextMenu
    {
    public:
        AlbumContextMenu() { ++ActiveObjects; }
        ~AlbumContextMenu() { --ActiveObjects; }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
                return E_POINTER;
            *object = nullptr;

            if (IsEqualIID(interfaceId, __uuidof(IUnknown)) || IsEqualIID(interfaceId, __uuidof(IContextMenu)))
                *object = static_cast<IContextMenu*>(this);
            else if (IsEqualIID(interfaceId, __uuidof(IShellExtInit)))
                *object = static_cast<IShellExtInit*>(this);
            else
                return E_NOINTERFACE;

            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const ULONG remaining = --references_;
            if (remaining == 0)
                delete this;
            return remaining;
        }

        HRESULT STDMETHODCALLTYPE Initialize(PCIDLIST_ABSOLUTE, IDataObject* dataObject, HKEY) override
        {
            paths_.clear();
            if (dataObject == nullptr)
                return E_INVALIDARG;

            FORMATETC format{};
            format.cfFormat = CF_HDROP;
            format.dwAspect = DVASPECT_CONTENT;
            format.lindex = -1;
            format.tymed = TYMED_HGLOBAL;

            STGMEDIUM medium{};
            HRESULT result = dataObject->GetData(&format, &medium);
            if (FAILED(result))
                return S_OK;

            try
            {
                if (medium.tymed == TYMED_HGLOBAL && medium.hGlobal != nullptr)
                {
                    const HDROP drop = reinterpret_cast<HDROP>(medium.hGlobal);
                    const UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
                    std::vector<std::wstring> paths;
                    paths.reserve(count);
                    for (UINT index = 0; index < count; ++index)
                    {
                        const UINT length = DragQueryFileW(drop, index, nullptr, 0);
                        if (length == 0)
                            continue;

                        std::vector<wchar_t> path(length + 1);
                        if (DragQueryFileW(drop, index, path.data(), static_cast<UINT>(path.size())) != 0)
                            paths.emplace_back(path.data());
                    }
                    paths_ = std::move(paths);
                }
            }
            catch (const std::bad_alloc&)
            {
                result = E_OUTOFMEMORY;
            }
            catch (...)
            {
                result = E_FAIL;
            }

            ReleaseStgMedium(&medium);
            return result;
        }

        HRESULT STDMETHODCALLTYPE QueryContextMenu(HMENU menu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT flags) override
        {
            if ((flags & CMF_DEFAULTONLY) != 0 || paths_.empty())
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            if (idCmdFirst > idCmdLast)
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);

            MENUITEMINFOW item{};
            item.cbSize = sizeof(item);
            item.fMask = MIIM_ID | MIIM_STRING;
            item.wID = idCmdFirst;
            item.dwTypeData = const_cast<PWSTR>(MenuTitle);
            if (!InsertMenuItemW(menu, indexMenu, TRUE, &item))
                return E_FAIL;

            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 1);
        }

        HRESULT STDMETHODCALLTYPE InvokeCommand(LPCMINVOKECOMMANDINFO commandInfo) override
        {
            if (commandInfo == nullptr)
                return E_INVALIDARG;

            bool isOurCommand = false;
            if (commandInfo->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) &&
                (commandInfo->fMask & CMIC_MASK_UNICODE) != 0)
            {
                const auto* extendedInfo = reinterpret_cast<const CMINVOKECOMMANDINFOEX*>(commandInfo);
                if (IS_INTRESOURCE(extendedInfo->lpVerbW))
                    isOurCommand = LOWORD(reinterpret_cast<ULONG_PTR>(extendedInfo->lpVerbW)) == 0;
                else if (extendedInfo->lpVerbW != nullptr)
                    isOurCommand = lstrcmpiW(extendedInfo->lpVerbW, CommandVerbW) == 0;
            }
            else if (IS_INTRESOURCE(commandInfo->lpVerb))
            {
                isOurCommand = LOWORD(reinterpret_cast<ULONG_PTR>(commandInfo->lpVerb)) == 0;
            }
            else if (commandInfo->lpVerb != nullptr)
            {
                isOurCommand = lstrcmpiA(commandInfo->lpVerb, CommandVerbA) == 0;
            }

            if (!isOurCommand)
                return E_INVALIDARG;
            if (paths_.empty())
                return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

            try
            {
                return LaunchApplication(paths_);
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        HRESULT STDMETHODCALLTYPE GetCommandString(UINT_PTR command, UINT type, UINT*, LPSTR name, UINT maxLength) override
        {
            if (command != 0)
                return E_INVALIDARG;

            switch (type)
            {
            case GCS_VERBA:
                if (name == nullptr || maxLength == 0)
                    return E_INVALIDARG;
                lstrcpynA(name, CommandVerbA, static_cast<int>(maxLength));
                return S_OK;
            case GCS_VERBW:
                if (name == nullptr || maxLength == 0)
                    return E_INVALIDARG;
                lstrcpynW(reinterpret_cast<LPWSTR>(name), CommandVerbW, static_cast<int>(maxLength));
                return S_OK;
            case GCS_HELPTEXTA:
                if (name == nullptr || maxLength == 0)
                    return E_INVALIDARG;
                lstrcpynA(name, "Add the selected images to a OneDrive album.", static_cast<int>(maxLength));
                return S_OK;
            case GCS_HELPTEXTW:
                if (name == nullptr || maxLength == 0)
                    return E_INVALIDARG;
                lstrcpynW(reinterpret_cast<LPWSTR>(name), HelpText, static_cast<int>(maxLength));
                return S_OK;
            case GCS_VALIDATEA:
            case GCS_VALIDATEW:
                return S_OK;
            default:
                return E_NOTIMPL;
            }
        }

    private:
        std::atomic<ULONG> references_{ 1 };
        std::vector<std::wstring> paths_;
    };

    class AlbumClassFactory final : public IClassFactory
    {
    public:
        AlbumClassFactory() { ++ActiveObjects; }
        ~AlbumClassFactory() { --ActiveObjects; }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
                return E_POINTER;
            *object = nullptr;
            if (IsEqualIID(interfaceId, __uuidof(IUnknown)) || IsEqualIID(interfaceId, __uuidof(IClassFactory)))
                *object = static_cast<IClassFactory*>(this);
            else
                return E_NOINTERFACE;
            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const ULONG remaining = --references_;
            if (remaining == 0)
                delete this;
            return remaining;
        }

        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
                return E_POINTER;
            *object = nullptr;
            if (outer != nullptr)
                return CLASS_E_NOAGGREGATION;

            auto contextMenu = new (std::nothrow) AlbumContextMenu();
            if (contextMenu == nullptr)
                return E_OUTOFMEMORY;
            const HRESULT result = contextMenu->QueryInterface(interfaceId, object);
            contextMenu->Release();
            return result;
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
        {
            if (lock)
                ++ServerLocks;
            else
                --ServerLocks;
            return S_OK;
        }

    private:
        std::atomic<ULONG> references_{ 1 };
    };
}

extern "C" HRESULT __stdcall AlbumDllGetClassObject(REFCLSID classId, REFIID interfaceId, void** object)
{
    if (object == nullptr)
        return E_POINTER;
    *object = nullptr;
    if (!IsEqualCLSID(classId, CommandClassId))
        return CLASS_E_CLASSNOTAVAILABLE;

    auto factory = new (std::nothrow) AlbumClassFactory();
    if (factory == nullptr)
        return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}

extern "C" HRESULT __stdcall AlbumDllCanUnloadNow()
{
    return ActiveObjects.load() == 0 && ServerLocks.load() == 0 ? S_OK : S_FALSE;
}
