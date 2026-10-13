# -*- coding: utf-8 -*-
Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
Name "Agent Pointer"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\AgentPointer"
InstallDirRegKey HKCU "Software\AgentPointer" "InstallLocation"
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\AgentPointer.Desktop.exe"
!define MUI_FINISHPAGE_TEXT "$(InstalledText)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString InstalledText ${LANG_ENGLISH} "Agent Pointer is installed.$\r$\n$\r$\nLocation: $INSTDIR$\r$\n$\r$\nOpen it from your desktop or Start menu. Try the demo, then install agent skills in the app."
LangString InstalledText ${LANG_SIMPCHINESE} "Agent Pointer 已安装。$\r$\n$\r$\n安装位置：$INSTDIR$\r$\n$\r$\n可从桌面或开始菜单打开。先体验屏幕指引，再在应用内选择安装 agent 技能。"
LangString CloseApp ${LANG_ENGLISH} "Exit Agent Pointer from its tray menu before installing or uninstalling."
LangString CloseApp ${LANG_SIMPCHINESE} "请先从托盘菜单退出 Agent Pointer，再安装或卸载。"
LangString UnsupportedWindows ${LANG_ENGLISH} "This package requires Windows 10/11 on an x64 computer."
LangString UnsupportedWindows ${LANG_SIMPCHINESE} "此安装包需要 Windows 10/11 和 x64 电脑。"
LangString UninstallName ${LANG_ENGLISH} "Uninstall Agent Pointer"
LangString UninstallName ${LANG_SIMPCHINESE} "卸载 Agent Pointer"

Function .onInit
    SetShellVarContext current
    System::Call 'kernel32::GetUserDefaultUILanguage() i.r0'
    IntOp $0 $0 & 0x3ff
    StrCpy $LANGUAGE ${LANG_ENGLISH}
    ${If} $0 == 4
        StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
    ${EndIf}
    ${IfNot} ${AtLeastWin10}
    ${OrIfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "$(UnsupportedWindows)" /SD IDOK
        Abort
    ${EndIf}
    System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\AgentPointer.Desktop.v1") p.r0'
    ${If} $0 != 0
        System::Call 'kernel32::CloseHandle(p r0)'
        MessageBox MB_OK|MB_ICONEXCLAMATION "$(CloseApp)" /SD IDOK
        Abort
    ${EndIf}
FunctionEnd

Section "Install"
    SetOutPath "$INSTDIR"
    File /r "${PACKAGE_DIR}\*"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\Agent Pointer"
    CreateShortcut "$SMPROGRAMS\Agent Pointer\Agent Pointer.lnk" "$INSTDIR\AgentPointer.Desktop.exe"
    CreateShortcut "$SMPROGRAMS\Agent Pointer\Uninstall.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0 SW_SHOWNORMAL "" "$(UninstallName)"
    CreateShortcut "$DESKTOP\Agent Pointer.lnk" "$INSTDIR\AgentPointer.Desktop.exe"
    WriteRegStr HKCU "Software\AgentPointer" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\AgentPointer" "InstallerLanguage" "$LANGUAGE"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "DisplayName" "Agent Pointer"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "DisplayVersion" "${VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "Publisher" "castorhrio"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "DisplayIcon" "$INSTDIR\AgentPointer.Desktop.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer" "NoRepair" 1
SectionEnd

Function un.onInit
    SetShellVarContext current
    ReadRegStr $LANGUAGE HKCU "Software\AgentPointer" "InstallerLanguage"
    System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\AgentPointer.Desktop.v1") p.r0'
    ${If} $0 != 0
        System::Call 'kernel32::CloseHandle(p r0)'
        MessageBox MB_OK|MB_ICONEXCLAMATION "$(CloseApp)" /SD IDOK
        Abort
    ${EndIf}
FunctionEnd

Section "Uninstall"
    ; Delete only files shipped by this build; preserve unrelated files and user settings.
    !include "${UNINSTALL_FILES}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    ReadRegStr $0 HKCU "Software\AgentPointer" "InstallLocation"
    ${If} $0 == $INSTDIR
        Delete "$DESKTOP\Agent Pointer.lnk"
        Delete "$SMPROGRAMS\Agent Pointer\Agent Pointer.lnk"
        Delete "$SMPROGRAMS\Agent Pointer\Uninstall.lnk"
        RMDir "$SMPROGRAMS\Agent Pointer"
        DeleteRegKey HKCU "Software\AgentPointer"
        DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer"
    ${EndIf}
SectionEnd
