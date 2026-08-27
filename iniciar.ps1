# ============================================================================
#  Abre o Howden Sales Forecast a partir de uma CÓPIA LOCAL do programa.
#
#  Por que: o executável é self-contained (~130 MB). Abrindo o atalho direto da
#  pasta de rede, o Windows precisa trazer esses 130 MB pela rede ANTES de o
#  programa começar — e por VPN isso leva minutos, toda vez.
#
#  Aqui o programa é copiado uma vez para a máquina e só é copiado de novo
#  quando sai uma versão nova. A BASE DE DADOS continua na rede, compartilhada
#  por todos: o que fica local é só o programa.
# ============================================================================

$ErrorActionPreference = 'Stop'

$rede  = Split-Path -Parent $MyInvocation.MyCommand.Path
$local = Join-Path $env:LOCALAPPDATA 'HowdenSalesForecast'
$nome  = 'Howden Sales Forecast.exe'

$exeRede  = Join-Path $rede  $nome
$exeLocal = Join-Path $local $nome
$carimbo  = Join-Path $local 'versao.txt'

if (-not (Test-Path $exeRede)) {
    Write-Host " Nao encontrei o programa em: $rede" -ForegroundColor Red
    Write-Host " Confira se a pasta de rede esta acessivel e tente de novo."
    Read-Host " Enter para fechar"
    exit 1
}

# Identidade desta versao: data de modificacao + tamanho do executavel. Ler isso
# custa um acesso minusculo a rede, contra os 130 MB de copiar o programa.
$fi     = Get-Item $exeRede
$versao = "$($fi.LastWriteTimeUtc.Ticks)|$($fi.Length)"
$atual  = if (Test-Path $carimbo) { (Get-Content $carimbo -Raw).Trim() } else { '' }

if ($atual -ne $versao -or -not (Test-Path $exeLocal)) {
    Write-Host ''
    Write-Host ' Versao nova encontrada. Copiando o programa para esta maquina...' -ForegroundColor Cyan
    Write-Host ' (so acontece quando sai atualizacao; as proximas aberturas sao imediatas)'
    Write-Host ''

    New-Item -ItemType Directory -Force -Path $local | Out-Null

    # O executavel anterior pode estar em uso por uma janela aberta. O Windows
    # nao deixa sobrescrever um .exe em uso, mas deixa RENOMEAR: o antigo sai da
    # frente, o novo entra no lugar e quem estava usando continua rodando.
    if (Test-Path $exeLocal) {
        try { Rename-Item $exeLocal ("old_{0}.exe" -f (Get-Random)) -ErrorAction Stop } catch { }
    }
    Get-ChildItem $local -Filter 'old_*.exe' -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { } }

    # /MIR espelha (remove sobras de versoes antigas). O launcher e os atalhos
    # ficam de fora: sao da pasta de rede, nao do programa.
    $log = robocopy $rede $local /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 `
                    /XF 'iniciar.ps1' 'iniciar.cmd' '*.lnk' 'old_*.exe' '*.old_*.exe' 'versao.txt'
    if ($LASTEXITCODE -ge 8) {
        Write-Host ' Falha ao copiar o programa. Verifique o acesso a pasta de rede.' -ForegroundColor Red
        Read-Host ' Enter para fechar'
        exit 1
    }
    Set-Content -Path $carimbo -Value $versao -Encoding ASCII
}

Start-Process -FilePath $exeLocal -WorkingDirectory $local
