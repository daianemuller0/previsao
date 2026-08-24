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
if "%DESTINO%"=="" set "DESTINO=\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\arquivo\02"
set "TEMPO=%TEMP%\hsf_publish"

echo.
echo  Destino: %DESTINO%
echo.
echo  Pode publicar mesmo com o programa aberto: o executavel anterior e
echo  renomeado e o novo entra no lugar. Quem estiver usando so vera a versao
echo  nova ao reabrir o atalho.
echo.
pause

REM --- 1) Publica numa pasta temporaria limpa -------------------------------
if exist "%TEMPO%" rmdir /s /q "%TEMPO%"

REM Self-contained em ARQUIVO UNICO: nao exige .NET instalado e deixa a pasta
REM publicada limpa (poucos arquivos).
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

REM --- 2) Libera o executavel anterior ---------------------------------------
REM O Windows NAO deixa sobrescrever um .exe em uso, mas DEIXA renomear. Entao
REM renomeamos o antigo (mesmo se alguem estiver com o programa aberto) e o novo
REM entra no lugar. Quem ja estava usando continua rodando na copia renomeada.
set "EXE=%DESTINO%\Howden Sales Forecast.exe"
if exist "%EXE%" (
  echo  Liberando o executavel anterior...
  ren "%EXE%" "Howden Sales Forecast.old_%RANDOM%%RANDOM%.exe" 2>nul
)

REM --- 3) Espelha no destino (remove sobras de versoes antigas) --------------
echo.
echo  Copiando para o destino...
REM /XF: nao mexe nos executaveis antigos renomeados (podem estar em uso).
robocopy "%TEMPO%" "%DESTINO%" /MIR /NFL /NDL /NJH /NP /R:2 /W:2 /XF "*.old_*.exe"
REM robocopy: codigos 0..7 = sucesso; 8+ = erro real.
if errorlevel 8 (
  echo.
  echo  *** Falha ao copiar para a pasta de rede. ***
  echo.
  echo  Verifique o acesso a pasta. Se o erro foi "Acesso negado" num arquivo,
  echo  feche o programa em todas as maquinas e rode de novo; persistindo,
  echo  apague o conteudo da pasta de destino e repita.
  echo.
  pause
  exit /b 1
)

REM Limpa os executaveis antigos que ninguem esta mais usando (os em uso ficam
REM para a proxima publicacao — a exclusao simplesmente falha e e ignorada).
del /q "%DESTINO%\*.old_*.exe" >nul 2>&1

REM --- 4) Cria o atalho com o icone (logo Howden, embutido no .exe) ----------
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
