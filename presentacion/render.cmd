@echo off
rem Renderiza la presentación con Node 18 (el node activo de esta PC es el 10, que no sirve para Remotion).
set "PATH=C:\Users\ssnunez\AppData\Roaming\nvm\v18.20.7;%PATH%"
cd /d "%~dp0"
if not exist node_modules call npm install
call npm run render
echo.
echo Listo: out\QueryAnalyzer-presentacion.mp4
