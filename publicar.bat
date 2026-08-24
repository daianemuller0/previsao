@echo off
REM ===========================================================================
REM  Publica o Howden Sales Forecast na pasta de rede e cria o atalho com o
REM  logo da Howden (o icone ja vem embutido no .exe).
REM
REM  Uso:  publicar.bat            -> publica no caminho padrao (abaixo)
REM        publicar.bat "D:\pasta" -> publica em outro caminho
REM ===========================================================================
setlocal

set "DESTINO=%~1"
if "%DESTINO%"=="" set "DESTINO=\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\arquivo\01"

echo.
echo  Publicando em: %DESTINO%
echo.

REM Self-contained: roda em qualquer maquina Windows, sem instalar o .NET.
dotnet publish "%~dp0HowdenSalesForecast.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%DESTINO%"

if errorlevel 1 (
  echo.
  echo  *** Falha ao publicar. Verifique o acesso a pasta de rede. ***
  pause
  exit /b 1
)

REM Cria o atalho na propria pasta publicada. O icone vem do .exe (logo Howden).
powershell -NoProfile -Command ^
  "$d='%DESTINO%';" ^
  "$exe=Join-Path $d 'Howden Sales Forecast.exe';" ^
  "$lnk=Join-Path $d 'Howden Sales Forecast.lnk';" ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($lnk);" ^
  "$s.TargetPath=$exe; $s.WorkingDirectory=$d; $s.IconLocation=\"$exe,0\";" ^
  "$s.Description='Howden Sales Forecast - Sales & Revenue Intelligence';" ^
  "$s.Save(); Write-Host ' Atalho criado: ' $lnk"

echo.
echo  Publicacao concluida.
echo  Para usar: abra o atalho "Howden Sales Forecast" na pasta publicada.
echo.
pause
