# ============================================================================
#  Cria o atalho "Howden Sales Forecast" na pasta publicada, com o logo.
#
#  Isto é um arquivo .ps1 chamado com -File, e não uma sequência de -Command
#  montada dentro do .bat: o caminho de rede tem "$" no nome (proj_ramires$) e
#  aspas atravessando cmd → PowerShell é justamente onde uma atribuição some em
#  silêncio. Aqui o script recebe a pasta como parâmetro e mais nada é
#  interpretado pelo cmd.
# ============================================================================
param([Parameter(Mandatory = $true)][string]$Destino)

$ErrorActionPreference = 'Stop'

$lnk = Join-Path $Destino 'Howden Sales Forecast.lnk'
$cmd = Join-Path $Destino 'iniciar.cmd'
$ico = Join-Path $Destino 'howden.ico'
$exe = Join-Path $Destino 'Howden Sales Forecast.exe'

# Apaga antes de recriar: o Windows guarda o ícone em cache e, regravando por
# cima, continuaria mostrando o anterior.
if (Test-Path $lnk) { Remove-Item $lnk -Force }

$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $cmd
$s.WorkingDirectory = $Destino
$s.Description = 'Howden Sales Forecast - Sales & Revenue Intelligence'
# O .ico ao lado é a fonte mais confiável; o ícone embutido no .exe é a reserva.
$s.IconLocation = if (Test-Path $ico) { "$ico,0" } else { "$exe,0" }
$s.Save()

# Relê o que ficou gravado: é a única forma de saber se o ícone pegou.
$conf = $sh.CreateShortcut($lnk)
Write-Host " Atalho criado: $lnk"
Write-Host " Icone do atalho: $($conf.IconLocation)"
if (-not $conf.IconLocation -or $conf.IconLocation -eq ',0') {
    Write-Host ' *** O icone NAO foi gravado no atalho. ***' -ForegroundColor Yellow
}

# Limpa o cache de icones do Explorer, senão a pasta continua mostrando o antigo.
try { & ie4uinit.exe -show } catch { }
