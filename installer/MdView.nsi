; MdView installer (NSIS)
Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define APP      "MdView"
!define VERSION  "1.1.0"
!define PUBLISHER "Davy Heutmekers"
!define PROGID   "MdView.Markdown"
!define UNINST   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"
!define BUILD    "../dist"

Name "${APP}"
OutFile "MdView-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP}"
InstallDirRegKey HKLM "${UNINST}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APP} Setup"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "${PUBLISHER}"

!define MUI_ICON   "../src/mdview.ico"
!define MUI_UNICON "../src/mdview.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\MdView.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Start MdView"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Show the README"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_WELCOMEPAGE_TEXT "This installs ${APP} ${VERSION}, a lightweight Markdown viewer.$\r$\n$\r$\nIt registers itself for .md and .markdown files. If another app is already your default for .md, you can switch to ${APP} afterwards via right-click > Open with > Choose another app.$\r$\n$\r$\nClick Next to continue."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro RegExt EXT
  WriteRegStr HKLM "Software\Classes\${EXT}\OpenWithProgids" "${PROGID}" ""
  WriteRegStr HKLM "Software\Classes\Applications\MdView.exe\SupportedTypes" "${EXT}" ""
  WriteRegStr HKLM "Software\${APP}\Capabilities\FileAssociations" "${EXT}" "${PROGID}"
  ; Become the default only if nothing claims the extension yet
  ReadRegStr $0 HKLM "Software\Classes\${EXT}" ""
  ${If} $0 == ""
    WriteRegStr HKLM "Software\Classes\${EXT}" "" "${PROGID}"
  ${EndIf}
!macroend

!macro UnregExt EXT
  DeleteRegValue HKLM "Software\Classes\${EXT}\OpenWithProgids" "${PROGID}"
  DeleteRegValue HKCU "Software\Classes\${EXT}\OpenWithProgids" "${PROGID}"
  ReadRegStr $0 HKLM "Software\Classes\${EXT}" ""
  ${If} $0 == "${PROGID}"
    DeleteRegValue HKLM "Software\Classes\${EXT}" ""
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Classes\${EXT}" ""
  ${If} $0 == "${PROGID}"
    DeleteRegValue HKCU "Software\Classes\${EXT}" ""
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APP} requires 64-bit Windows."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

Section "Install"
  nsExec::Exec 'taskkill /F /IM MdView.exe'
  Sleep 300

  ; --- remove the earlier per-user (script) install, if present ---
  SetShellVarContext current
  RMDir /r "$LOCALAPPDATA\Programs\${APP}"
  Delete "$SMPROGRAMS\${APP}.lnk"
  DeleteRegKey HKCU "Software\Classes\${PROGID}"
  DeleteRegKey HKCU "Software\Classes\Applications\MdView.exe"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe"
  DeleteRegKey HKCU "Software\${APP}"
  DeleteRegValue HKCU "Software\RegisteredApplications" "${APP}"

  ; --- files ---
  SetOutPath "$INSTDIR"
  File "${BUILD}/MdView.exe"
  File "${BUILD}/Microsoft.Web.WebView2.Core.dll"
  File "${BUILD}/Microsoft.Web.WebView2.WinForms.dll"
  File "${BUILD}/README.md"
  File /r "${BUILD}/runtimes"
  File /r "${BUILD}/web"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; --- file type + app registration (all users) ---
  WriteRegStr HKLM "Software\Classes\${PROGID}" "" "Markdown document"
  WriteRegStr HKLM "Software\Classes\${PROGID}\DefaultIcon" "" '"$INSTDIR\MdView.exe",0'
  WriteRegStr HKLM "Software\Classes\${PROGID}\shell\open\command" "" '"$INSTDIR\MdView.exe" "%1"'
  WriteRegStr HKLM "Software\Classes\Applications\MdView.exe" "FriendlyAppName" "${APP}"
  WriteRegStr HKLM "Software\Classes\Applications\MdView.exe\DefaultIcon" "" '"$INSTDIR\MdView.exe",0'
  WriteRegStr HKLM "Software\Classes\Applications\MdView.exe\shell\open\command" "" '"$INSTDIR\MdView.exe" "%1"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe" "" "$INSTDIR\MdView.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe" "Path" "$INSTDIR"
  WriteRegStr HKLM "Software\${APP}\Capabilities" "ApplicationName" "${APP}"
  WriteRegStr HKLM "Software\${APP}\Capabilities" "ApplicationDescription" "Lightweight Markdown viewer"
  WriteRegStr HKLM "Software\${APP}\Capabilities" "ApplicationIcon" '"$INSTDIR\MdView.exe",0'
  WriteRegStr HKLM "Software\RegisteredApplications" "${APP}" "Software\${APP}\Capabilities"
  !insertmacro RegExt ".md"
  !insertmacro RegExt ".markdown"
  !insertmacro RegExt ".mdown"
  !insertmacro RegExt ".mkd"
  !insertmacro RegExt ".mkdn"

  ; --- Apps & features entry ---
  WriteRegStr   HKLM "${UNINST}" "DisplayName" "${APP}"
  WriteRegStr   HKLM "${UNINST}" "DisplayVersion" "${VERSION}"
  WriteRegStr   HKLM "${UNINST}" "Publisher" "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINST}" "DisplayIcon" '"$INSTDIR\MdView.exe",0'
  WriteRegStr   HKLM "${UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINST}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINST}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINST}" "EstimatedSize" 5200

  ; --- Start menu (all users) ---
  SetShellVarContext all
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\MdView.exe" "" "$INSTDIR\MdView.exe" 0 SW_SHOWNORMAL "" "Markdown viewer"

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM MdView.exe'
  Sleep 300

  SetShellVarContext all
  Delete "$SMPROGRAMS\${APP}.lnk"

  !insertmacro UnregExt ".md"
  !insertmacro UnregExt ".markdown"
  !insertmacro UnregExt ".mdown"
  !insertmacro UnregExt ".mkd"
  !insertmacro UnregExt ".mkdn"
  DeleteRegKey HKLM "Software\Classes\${PROGID}"
  DeleteRegKey HKLM "Software\Classes\Applications\MdView.exe"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe"
  DeleteRegValue HKLM "Software\RegisteredApplications" "${APP}"
  DeleteRegKey HKLM "Software\${APP}"
  DeleteRegKey HKLM "${UNINST}"

  RMDir /r "$INSTDIR\runtimes"
  RMDir /r "$INSTDIR\web"
  Delete "$INSTDIR\MdView.exe"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  ; Per-user settings and WebView2 cache of the user running the uninstaller
  SetShellVarContext current
  RMDir /r "$LOCALAPPDATA\${APP}"

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
