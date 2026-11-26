; Windows installer for VnExpress GIF Maker (built on the Mac by windows/build.sh with makensis).
; Per-user install (no admin rights): %LOCALAPPDATA%\Programs\VnExpress GIF Maker.
; /S = silent; /UPDATE = launched by the in-app updater: wait for the app to close, relaunch after.
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!define APPNAME "VnExpress GIF Maker"
!define EXE "VnExpressGifMaker.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\VNEGifMaker"
!ifndef VERSION
  !define VERSION "1.0"
!endif

Name "${APPNAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
BrandingText "Lâm Nguyễn · Ban Video, VnExpress"
VIProductVersion "${VERSION}.0.0"
VIAddVersionKey /LANG=0 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=0 "FileDescription" "Cài đặt ${APPNAME}"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "CompanyName" "Ban Video, VnExpress"
VIAddVersionKey /LANG=0 "LegalCopyright" "Lâm Nguyễn · Ban Video, VnExpress"

!include "MUI2.nsh"
!include "FileFunc.nsh"
!define MUI_ICON "..\src\VneGif.App\Assets\AppIcon.ico"
!define MUI_UNICON "..\src\VneGif.App\Assets\AppIcon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Mở ${APPNAME}"
!define MUI_WELCOMEPAGE_TITLE "Cài đặt ${APPNAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Công cụ cắt và ghép video thành GIF ngang dưới 8 MB, làm thumbnail cho VnExpress.$\r$\n$\r$\nỨng dụng được cài cho riêng tài khoản Windows này, không cần quyền quản trị.$\r$\n$\r$\nBấm Tiếp để tiếp tục."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Vietnamese"

Var Updating

Function .onInit
  StrCpy $Updating 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  IfErrors +2 0
    StrCpy $Updating 1
FunctionEnd

; The running app keeps its .exe locked: wait (up to ~20 s) until it has closed.
!macro WaitForApp
  StrCpy $0 0
  wait_loop:
    IfFileExists "$INSTDIR\${EXE}" 0 wait_done
    ClearErrors
    Delete "$INSTDIR\${EXE}"
    IfErrors 0 wait_done
    IntOp $0 $0 + 1
    IntCmp $0 40 wait_done
    Sleep 500
    Goto wait_loop
  wait_done:
!macroend

Section "Install"
  SetOutPath "$INSTDIR"
  !insertmacro WaitForApp
  File /r "${STAGE}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXE}"
  StrCmp $Updating 1 +2 0
    CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Ban Video, VnExpress"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" $0
  StrCmp $Updating 1 0 +2
    Exec '"$INSTDIR\${EXE}"'
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
  ; Settings, session and caches in %APPDATA% / %LOCALAPPDATA%\VNEGifMaker are kept.
SectionEnd
