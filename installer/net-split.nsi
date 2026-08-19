Unicode true
!include "LogicLib.nsh"
RequestExecutionLevel admin
Name "net-split"
OutFile "..\artifacts\net-split-setup.exe"
InstallDir "$PROGRAMFILES64\net-split"
ShowInstDetails show
ShowUninstDetails show

Section "Install"
  SetOutPath "$INSTDIR\service"
  File /r "..\artifacts\win-x64\service\*"
  SetOutPath "$INSTDIR\tray"
  File /r "..\artifacts\win-x64\tray\*"
  SetOutPath "$INSTDIR\recovery"
  File /r "..\artifacts\win-x64\recovery\*"
  SetOutPath "$INSTDIR"
  File "..\scripts\install.ps1"
  File "..\scripts\uninstall.ps1"

  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\install.ps1" -PublishRoot "$INSTDIR"'
  Pop $0
  StrCmp $0 "0" install_ok
    DetailPrint "Installation script failed with exit code $0. Rolling back."
    nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall.ps1" -PublishRoot "$INSTDIR"'
    Pop $1
    Abort "net-split installation failed. See the installer log."
  install_ok:
  WriteUninstaller "$INSTDIR\uninstall.exe"
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall.ps1" -PublishRoot "$INSTDIR"'
  Pop $0
  StrCmp $0 "0" uninstall_ok
    Abort "net-split cleanup failed. Installation files were retained."
  uninstall_ok:
  RMDir /r "$INSTDIR"
SectionEnd
