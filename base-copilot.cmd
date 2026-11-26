@echo off
REM ===========================================================================
REM  Base do agente do Copilot SEM abrir o programa.
REM
REM  Roda o Howden Sales Forecast "escondido": importa as listagens de NB e AFM
REM  da pasta de rede, grava o Base_Previsao_NB_AFM.xlsx na pasta do SharePoint/
REM  OneDrive e fecha. Feito para a tarefa agendada do Windows, alguns minutos
REM  depois das listagens das 08:00 e das 13:00.
REM
REM  Uso (uma vez, no computador que tem a pasta do SharePoint sincronizada):
REM    base-copilot.cmd instalar "C:\Users\voce\OneDrive - Howden\Copilot"
REM      -> cria as tarefas "Howden Base Copilot 08h20" e "... 13h20" (seg a sex)
REM    base-copilot.cmd gerar "C:\...\Copilot"     -> gera agora (teste)
REM    base-copilot.cmd remover                     -> apaga as tarefas
REM
REM  O resultado de cada execucao fica no subida.log, ao lado do programa.
REM ===========================================================================
setlocal
set "ACAO=%~1"
set "PASTA=%~2"
set "EXE=%~dp0Howden Sales Forecast.exe"

if /i "%ACAO%"=="gerar" goto gerar
if /i "%ACAO%"=="instalar" goto instalar
if /i "%ACAO%"=="remover" goto remover
echo Uso: base-copilot.cmd instalar "C:\pasta\do\SharePoint"  ^|  gerar "C:\pasta"  ^|  remover
exit /b 1

:gerar
if "%PASTA%"=="" (echo Informe a pasta do SharePoint/OneDrive. & exit /b 1)
if not exist "%PASTA%" (echo A pasta "%PASTA%" nao existe neste computador. & exit /b 1)
"%EXE%" --BaseCopilot=true --OpenBrowser=false "--Copilot:Pasta=%PASTA%"
if errorlevel 1 (echo Nao foi possivel gerar a base: veja o subida.log ao lado do programa. & exit /b 1)
echo Base do Copilot gravada em "%PASTA%".
exit /b 0

:instalar
if "%PASTA%"=="" (echo Informe a pasta do SharePoint/OneDrive. & exit /b 1)
if not exist "%PASTA%" (echo A pasta "%PASTA%" nao existe neste computador. & exit /b 1)
schtasks /Create /F /TN "Howden Base Copilot 08h20" /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 08:20 /TR "\"%~f0\" gerar \"%PASTA%\"" || exit /b 1
schtasks /Create /F /TN "Howden Base Copilot 13h20" /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 13:20 /TR "\"%~f0\" gerar \"%PASTA%\"" || exit /b 1
echo.
echo Pronto: a base sera gerada de segunda a sexta as 08:20 e as 13:20
echo (com o computador ligado e voce conectado ao Windows).
exit /b 0

:remover
schtasks /Delete /F /TN "Howden Base Copilot 08h20"
schtasks /Delete /F /TN "Howden Base Copilot 13h20"
exit /b 0
