# ============================================================================
#  Cria o atalho "Howden Sales Forecast" com o logo da Howden.
#
#  Isto é um arquivo .ps1 chamado com -File, e não uma sequência de -Command
#  montada dentro do .bat: o caminho de rede tem "$" no nome (proj_ramires$) e
#  aspas atravessando cmd → PowerShell é justamente onde uma atribuição some em
#  silêncio. Aqui o script recebe a pasta como parâmetro e mais nada é
#  interpretado pelo cmd.
#
#  -Destino  pasta onde o .lnk é GRAVADO (e onde estão os arquivos consultados)
#  -Alvo     pasta para onde o atalho APONTA; por padrão, a mesma do -Destino
#
#  A publicação grava o atalho na pasta temporária apontando para a rede, e o
#  robocopy /MIR leva o .lnk junto com o resto. Antes o atalho era criado
#  depois do espelhamento, e qualquer tropeço aqui deixava a pasta publicada
#  sem atalho nenhum — porque o /MIR já tinha apagado o antigo.
# ============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Destino,
    [string]$Alvo
)

if ([string]::IsNullOrWhiteSpace($Alvo)) { $Alvo = $Destino }

# Sem 'Stop' global: um erro solto aqui encerrava o script no meio e a única
# pista era uma linha vermelha passando no meio da publicação. Agora cada falha
# é dita em português e vira código de saída 1, que o publicar.bat sabe ler.
$ErrorActionPreference = 'Continue'

$lnk = Join-Path $Destino 'Howden Sales Forecast.lnk'
$cmd = Join-Path $Alvo 'iniciar.cmd'
$ico = Join-Path $Alvo 'howden.ico'
$exe = Join-Path $Alvo 'Howden Sales Forecast.exe'

# O .ico ao lado é a fonte mais confiável; o ícone embutido no .exe é a reserva.
# A existência é conferida na pasta de gravação (é lá que os arquivos acabaram
# de ser colocados), mas o que vai escrito no atalho é o caminho do alvo.
$temIco = Test-Path (Join-Path $Destino 'howden.ico')

try {
    # Apaga antes de recriar: o Windows guarda o ícone em cache e, regravando
    # por cima, continuaria mostrando o anterior.
    if (Test-Path $lnk) { Remove-Item $lnk -Force -ErrorAction Stop }

    $sh = New-Object -ComObject WScript.Shell -ErrorAction Stop
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $cmd
    $s.WorkingDirectory = $Alvo
    $s.Description = 'Howden Sales Forecast - Sales & Revenue Intelligence'
    $s.IconLocation = if ($temIco) { "$ico,0" } else { "$exe,0" }
    $s.Save()
}
catch {
    Write-Host ''
    Write-Host ' *** Nao foi possivel criar o atalho. ***' -ForegroundColor Red
    Write-Host "     Pasta: $Destino"
    Write-Host "     Erro:  $($_.Exception.Message)"
    exit 1
}

if (-not (Test-Path $lnk)) {
    Write-Host ''
    Write-Host ' *** O atalho nao foi gravado (a pasta aceitou o comando mas o arquivo nao ficou). ***' -ForegroundColor Red
    Write-Host "     Pasta: $Destino"
    exit 1
}

# Relê o que ficou gravado: é a única forma de saber se o ícone pegou.
$conf = $sh.CreateShortcut($lnk)
Write-Host " Atalho criado: $lnk"
Write-Host " Aponta para:   $cmd"
Write-Host " Icone:         $($conf.IconLocation)"
if (-not $conf.IconLocation -or $conf.IconLocation -eq ',0') {
    Write-Host ' *** O icone NAO foi gravado no atalho. ***' -ForegroundColor Yellow
}

# Limpa o cache de ícones do Explorer, senão a pasta continua mostrando o antigo.
try { & ie4uinit.exe -show } catch { }

exit 0
