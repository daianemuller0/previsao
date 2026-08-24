@echo off
REM ===========================================================================
REM  Publica o Howden Sales Forecast na pasta de rede e cria o atalho com o
REM  logo da Howden (o icone ja vem embutido no .exe).
REM
REM  Publica primeiro numa pasta temporaria local e depois ESPELHA na pasta de
REM  destino (robocopy /MIR): assim os arquivos de versoes antigas sao removidos
REM  e nao sobra executavel velho para alguem abrir por engano.
REM
REM  A BASE DE DADOS NAO E AFETADA: ela fica em ...\previsao (pasta acima),
REM  fora da pasta do programa.
REM
REM  Uso:  publicar.bat            -> publica no caminho padrao (abaixo)
REM        publicar.bat "D:\pasta" -> publica em outro caminho
REM ===========================================================================
setlocal

set "DESTINO=%~1"
if "%DESTINO%"=="" set "DESTINO=\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\arquivo\01"
set "TEMPO=%TEMP%\hsf_publish"

echo.
echo  Destino: %DESTINO%
echo.
echo  ATENCAO: feche o programa se ele estiver aberto (aqui ou em outra
echo           maquina), senao os arquivos ficam travados.
echo.
pause

REM --- 1) Publica numa pasta temporaria limpa -------------------------------
if exist "%TEMPO%" rmdir /s /q "%TEMPO%"

dotnet publish "%~dp0HowdenSalesForecast.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%TEMPO%"

if errorlevel 1 (
  echo.
  echo  *** Falha ao compilar. Veja os erros acima. ***
  pause
  exit /b 1
)

REM --- 2) Espelha no destino (remove sobras de versoes antigas) --------------
echo.
echo  Copiando para o destino...
robocopy "%TEMPO%" "%DESTINO%" /MIR /NFL /NDL /NJH /NP /R:2 /W:2
REM robocopy: codigos 0..7 = sucesso; 8+ = erro real.
if errorlevel 8 (
  echo.
  echo  *** Falha ao copiar para a pasta de rede. Verifique acesso/arquivos em uso. ***
  pause
  exit /b 1
)

REM --- 3) Cria o atalho com o icone (logo Howden, embutido no .exe) ----------
powershell -NoProfile -Command ^
  "$d='%DESTINO%';" ^
  "$exe=Join-Path $d 'Howden Sales Forecast.exe';" ^
  "$lnk=Join-Path $d 'Howden Sales Forecast.lnk';" ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($lnk);" ^
  "$s.TargetPath=$exe; $s.WorkingDirectory=$d; $s.IconLocation=\"$exe,0\";" ^
  "$s.Description='Howden Sales Forecast - Sales & Revenue Intelligence';" ^
  "$s.Save(); Write-Host ' Atalho criado: ' $lnk"

rmdir /s /q "%TEMPO%"

echo.
echo  Publicacao concluida.
echo  Para usar: abra o atalho "Howden Sales Forecast" na pasta publicada.
echo.
pause
