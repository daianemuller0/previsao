' ===========================================================================
'  Abre o Howden Sales Forecast sem nenhuma janela preta.
'
'  Compara a copia local do programa com a da rede (tamanho + data). Se estao
'  iguais, abre a local direto e oculto — nada pisca na tela. Se saiu versao
'  nova, chama o iniciar.cmd COM janela, para a pessoa ver a copia acontecendo
'  em vez de achar que o programa travou.
'
'  A BASE DE DADOS continua na rede: o que fica local e so o programa.
' ===========================================================================
Option Explicit

Dim fso, sh, rede, nome, exeRede, pastaLocal, exeLocal, atualizado

Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

rede    = fso.GetParentFolderName(WScript.ScriptFullName)
nome    = "Howden Sales Forecast.exe"
exeRede = fso.BuildPath(rede, nome)

If Not fso.FileExists(exeRede) Then
    MsgBox "Nao encontrei o programa em:" & vbCrLf & vbCrLf & rede & vbCrLf & vbCrLf & _
           "Confira se a pasta de rede esta acessivel.", vbExclamation, "Howden Sales Forecast"
    WScript.Quit 1
End If

pastaLocal = fso.BuildPath(sh.ExpandEnvironmentStrings("%LOCALAPPDATA%"), "HowdenSalesForecast")
exeLocal   = fso.BuildPath(pastaLocal, nome)

' Tamanho + data de modificacao identificam a versao. O robocopy preserva a
' data ao copiar, entao os dois batem exatamente enquanto nao houver publicacao
' nova — e comparar isso custa uma leitura minuscula, nao os 130 MB do programa.
atualizado = False
If fso.FileExists(exeLocal) Then
    Dim a, b
    Set a = fso.GetFile(exeRede)
    Set b = fso.GetFile(exeLocal)
    atualizado = (a.Size = b.Size) And (a.DateLastModified = b.DateLastModified)
End If

If atualizado Then
    sh.Run """" & exeLocal & """", 0, False                       ' 0 = sem janela
Else
    sh.Run """" & fso.BuildPath(rede, "iniciar.cmd") & """", 1, False  ' 1 = mostra a copia
End If
