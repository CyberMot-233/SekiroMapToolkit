@echo off
chcp 65001 >nul
setlocal
rem ============================================================
rem  只狼音频一键导出（全部音库 → MP3）—— 命令行模式
rem  解包目录 / 输出目录 按需改这两行
rem ============================================================
set "ROOT=E:\SteamLibrary\steamapps\common\Sekiro\文件"
set "OUT=%~dp0输出\音频"
set "EXE=%~dp0SekiroMapToolkit.exe"

if not exist "%EXE%" (
  echo [错误] 同目录下找不到 SekiroMapToolkit.exe
  pause & exit /b 1
)
if not exist "%ROOT%\sound" (
  echo [错误] 找不到音库目录：%ROOT%\sound
  echo        请先用 Nuxe / UXM 解包，并勾上 sound 项
  pause & exit /b 1
)

echo 开始导出音频：%ROOT%\sound  -^>  %OUT%
echo （首次会自动下载 vgmstream 解码器，约 4 MB）
echo.
"%EXE%" --cli --audio --sound "%ROOT%\sound" --audio-out "%OUT%" --all
echo.
echo 导出完成，日志：%OUT%\cli_audio.log
echo 成品目录：%OUT%\^<地图^>\^<类别^>\^<音库^>\*.mp3
pause