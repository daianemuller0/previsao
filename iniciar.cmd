@echo off
REM ===========================================================================
REM  Atalho de abertura do Howden Sales Forecast.
REM  Chama o iniciar.ps1 ao lado, que mantem uma copia local do programa e so
REM  copia de novo quando sai versao nova (abrir 130 MB pela VPN toda vez e o
REM  que fazia o programa demorar minutos para aparecer).
REM
REM  A janela fica visivel de proposito: e por ela que se acompanha a copia
REM  quando sai versao nova, e ela se fecha sozinha assim que o programa abre.
REM ===========================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0iniciar.ps1"
