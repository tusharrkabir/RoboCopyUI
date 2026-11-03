#include <windows.h>
#include <shellapi.h>
#include <string>

static std::wstring Quote(const std::wstring& input) {
    std::wstring result=L"\"";size_t slashes=0;
    for(auto c:input){if(c==L'\\'){++slashes;continue;}if(c==L'"'){result.append(slashes*2+1,L'\\');result+=c;}else{result.append(slashes,L'\\');result+=c;}slashes=0;}
    result.append(slashes*2,L'\\');result+=L'"';return result;
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    wchar_t location[32768];const auto length=GetModuleFileNameW(nullptr,location,32768);
    if(!length||length>=32768)return 1;
    const std::wstring executable(location);const auto slash=executable.find_last_of(L"\\/");
    if(slash==std::wstring::npos)return 1;const auto root=executable.substr(0,slash);
    const auto runtime=root+L"\\runtime\\dotnet.exe";
    const auto assembly=root+L"\\App\\RoboCopyUI.dll";
    int count=0;auto args=CommandLineToArgvW(GetCommandLineW(),&count);if(!args)return 1;
    auto command=Quote(runtime)+L" "+Quote(assembly);
    for(int i=1;i<count;++i)command+=L" "+Quote(args[i]);LocalFree(args);
    STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
    if(!CreateProcessW(runtime.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&startup,&process)) {
        MessageBoxW(nullptr,L"RoboCopyUI 的随包运行时无法启动。请使用安装包修复安装。",L"RoboCopyUI",MB_OK|MB_ICONERROR);return 1;
    }
    CloseHandle(process.hThread);WaitForSingleObject(process.hProcess,INFINITE);
    DWORD code=1;GetExitCodeProcess(process.hProcess,&code);CloseHandle(process.hProcess);return static_cast<int>(code);
}
