@echo off
REM ===========================================================================
REM  Atalho de abertura do Howden Sales Forecast.
REM  Chama o iniciar.ps1 ao lado, que mantem uma copia local do programa e so
REM  copia de novo quando sai versao nova (abrir 130 MB pela VPN toda vez e o
REM  que fazia o programa demorar minutos para aparecer).
REM ===========================================================================
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0iniciar.ps1"
