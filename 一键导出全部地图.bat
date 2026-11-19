@echo off
chcp 65001 >nul
setlocal
rem ============================================================
rem  只狼地图一键导出（全部地图）—— 命令行模式
rem  解包目录 / 输出目录 按需改这两行
rem ============================================================
set "ROOT=E:\SteamLibrary\steamapps\common\Sekiro\文件"
set "OUT=%~dp0输出"
set "EXE=%~dp0SekiroMapToolkit.exe"

if not exist "%EXE%" (
  echo [错误] 同目录下找不到 SekiroMapToolkit.exe
  pause & exit /b 1
)
if not exist "%ROOT%\map\mapstudio" (
  echo [错误] 解包目录不对：%ROOT%
  echo        请先确认 <游戏根>\文件\map\mapstudio 存在
  pause & exit /b 1
)

echo 开始导出：%ROOT%  ->  %OUT%
"%EXE%" --cli --root "%ROOT%" --out "%OUT%" --blend
echo.
echo 导出完成，日志：%OUT%\cli_export.log
echo 成品目录：%OUT%\<地图名>\<地图名>.blend
pause