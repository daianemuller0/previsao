Attribute VB_Name = "modPrevisao"
' =====================================================================
'  modPrevisao - salva os anexos Excel dos e-mails de previsao
'                nas pastas da rede, com nome fixo (sobrescreve).
'
'  Cole este modulo no VBA do Outlook (Alt+F11):
'      Inserir > Modulo  ->  colar todo o conteudo abaixo
'
'  Como usar (3 formas, na ordem de confiabilidade):
'      1) VarrerCaixaDeEntrada  - varre os ultimos N dias e salva tudo.
'                                 Rode na mao ou agende (ver README).
'      2) ItemAdd (ThisOutlookSession) - salva sozinho quando o e-mail
'                                 chega, com o Outlook aberto.
'      3) Regra "executar script" - so funciona com a chave de registro
'                                 habilitada (ver README).
'
'  Diagnostico: rode TestarPastas antes de tudo.
' =====================================================================

Option Explicit

' ===== Pastas de destino (NAO alterar as barras) =====
Private Const PASTA_NB   As String = "\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\NB\"
Private Const PASTA_AFM  As String = "\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\planilha\AFM\"
Private Const PASTA_FUP  As String = "\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\FUP\"

' Quantos dias para tras a varredura olha
Private Const DIAS_VARREDURA As Long = 7

' Arquivo de log (deixe "" para nao gravar log)
Private Const ARQUIVO_LOG As String = "\\BZVCPFIL003\proj_ramires$\DB\AFM_HSA\previsao\_log_previsao.txt"


' ---------------------------------------------------------------------
'  1) VARREDURA MANUAL - o jeito mais confiavel
'     Selecione esta Sub e aperte F5.
' ---------------------------------------------------------------------
Public Sub VarrerCaixaDeEntrada()
    Dim ns As Outlook.NameSpace
    Dim fldEntrada As Outlook.Folder
    Dim itens As Outlook.Items
    Dim itensFiltrados As Outlook.Items
    Dim obj As Object
    Dim sFiltro As String
    Dim nSalvos As Long
    Dim nEmails As Long

    On Error GoTo Erro

    Set ns = Application.GetNamespace("MAPI")
    Set fldEntrada = ns.GetDefaultFolder(olFolderInbox)
    Set itens = fldEntrada.Items

    ' Ordena do mais antigo para o mais novo: assim, se o mesmo relatorio
    ' chegou varias vezes, o ULTIMO salvo e o mais recente.
    itens.Sort "[ReceivedTime]", False

    sFiltro = "[ReceivedTime] >= '" & _
              Format(Date - DIAS_VARREDURA, "ddddd") & " 00:00'"
    Set itensFiltrados = itens.Restrict(sFiltro)

    For Each obj In itensFiltrados
        If TypeOf obj Is Outlook.MailItem Then
            nEmails = nEmails + 1
            nSalvos = nSalvos + SalvarPrevisao(obj)
        End If
    Next

    MsgBox "Varredura concluida." & vbCrLf & vbCrLf & _
           "E-mails lidos (ultimos " & DIAS_VARREDURA & " dias): " & nEmails & vbCrLf & _
           "Arquivos salvos: " & nSalvos, _
           vbInformation, "Previsao"
    Exit Sub

Erro:
    MsgBox "Erro na varredura: " & Err.Number & " - " & Err.Description, _
           vbExclamation, "Previsao"
End Sub


' ---------------------------------------------------------------------
'  Salva os anexos de UM e-mail. Retorna quantos arquivos gravou.
'  Esta e a Sub/Function que a regra ou o ItemAdd chamam.
' ---------------------------------------------------------------------
Public Function SalvarPrevisao(MItem As Outlook.MailItem) As Long
    Dim oAtt As Outlook.Attachment
    Dim sAssunto As String
    Dim sPasta As String
    Dim sNomeAlvo As String
    Dim sExt As String
    Dim sDestino As String
    Dim nPonto As Long
    Dim nSalvos As Long

    On Error GoTo Erro

    SalvarPrevisao = 0
    If MItem Is Nothing Then Exit Function

    sAssunto = LCase$(Trim$(MItem.Subject & ""))
    If Len(sAssunto) = 0 Then Exit Function

    ' ===== Mapeamento ASSUNTO -> PASTA + NOME FIXO =====
    Select Case True
        Case InStr(sAssunto, LCase$("Report results (FUP-HSA_Salesforce)")) > 0
            sPasta = PASTA_FUP: sNomeAlvo = "FUP_NB"
        Case InStr(sAssunto, LCase$("Report results (NB_WON_SF)")) > 0
            sPasta = PASTA_FUP: sNomeAlvo = "WON_NB"
        Case InStr(sAssunto, LCase$("Report results (Funil de Vendas HSA)")) > 0
            sPasta = PASTA_NB:  sNomeAlvo = "Funil_NB"
        Case InStr(sAssunto, LCase$("AFM_FUP (Daily)")) > 0
            sPasta = PASTA_FUP: sNomeAlvo = "FUP_AFM"
        Case InStr(sAssunto, LCase$("AFM_Won (Daily)")) > 0
            sPasta = PASTA_FUP: sNomeAlvo = "WON_AFM"
        Case InStr(sAssunto, LCase$("Funil_AFM (Daily)")) > 0
            sPasta = PASTA_AFM: sNomeAlvo = "Funil_AFM"
        Case Else
            Exit Function          ' assunto nao mapeado: ignora
    End Select

    If MItem.Attachments.Count = 0 Then
        Gravar "SEM ANEXO | " & MItem.Subject
        Exit Function
    End If

    If Not PastaExiste(sPasta) Then
        Gravar "PASTA INDISPONIVEL | " & sPasta & " | " & MItem.Subject
        Exit Function
    End If

    ' ===== Salva o(s) anexo(s) Excel, sobrescrevendo =====
    For Each oAtt In MItem.Attachments

        ' Ignora imagens embutidas na assinatura / corpo do e-mail
        If oAtt.Type <> olOLE Then

            nPonto = InStrRev(oAtt.FileName, ".")
            If nPonto > 0 Then
                sExt = LCase$(Mid$(oAtt.FileName, nPonto + 1))
            Else
                sExt = ""
            End If

            If sExt = "xlsx" Or sExt = "xls" Or sExt = "xlsm" Or sExt = "csv" Then
                sDestino = sPasta & sNomeAlvo & "." & sExt

                On Error Resume Next
                Err.Clear
                oAtt.SaveAsFile sDestino
                If Err.Number = 0 Then
                    nSalvos = nSalvos + 1
                    Gravar "OK | " & sDestino & " | " & MItem.Subject
                Else
                    ' erro tipico: arquivo aberto no Excel por alguem
                    Gravar "FALHA (" & Err.Number & " " & Err.Description & ") | " & _
                           sDestino & " | " & MItem.Subject
                    Err.Clear
                End If
                On Error GoTo Erro
            End If

        End If
    Next

    SalvarPrevisao = nSalvos
    Exit Function

Erro:
    Gravar "ERRO " & Err.Number & " - " & Err.Description & " | " & MItem.Subject
    SalvarPrevisao = nSalvos
End Function


' ---------------------------------------------------------------------
'  Wrapper para a acao de regra "executar um script".
'  A regra so aceita Sub publica com UM parametro MailItem.
' ---------------------------------------------------------------------
Public Sub RegraSalvarPrevisao(MItem As Outlook.MailItem)
    Dim n As Long
    n = SalvarPrevisao(MItem)
End Sub


' ---------------------------------------------------------------------
'  2) DIAGNOSTICO - rode isto primeiro se nada estiver funcionando
' ---------------------------------------------------------------------
Public Sub TestarPastas()
    Dim s As String

    s = "Acesso as pastas de destino:" & vbCrLf & vbCrLf
    s = s & Marcar(PASTA_NB) & vbCrLf
    s = s & Marcar(PASTA_AFM) & vbCrLf
    s = s & Marcar(PASTA_FUP) & vbCrLf & vbCrLf

    If Len(ARQUIVO_LOG) > 0 Then
        s = s & "Log: " & ARQUIVO_LOG & vbCrLf & vbCrLf
    End If

    s = s & "Se aparecer NAO ACESSIVEL, o problema e rede/permissao," & vbCrLf & _
            "nao a macro. Abra o caminho no Explorer para confirmar."

    MsgBox s, vbInformation, "Previsao - diagnostico"
End Sub

Private Function Marcar(sPasta As String) As String
    If PastaExiste(sPasta) Then
        Marcar = "[ OK ]            " & sPasta
    Else
        Marcar = "[ NAO ACESSIVEL ] " & sPasta
    End If
End Function


' ---------------------------------------------------------------------
'  Mostra quais assuntos dos ultimos dias BATERAM no mapeamento.
'  Util quando o assunto real e diferente do esperado.
' ---------------------------------------------------------------------
Public Sub ListarAssuntos()
    Dim ns As Outlook.NameSpace
    Dim itens As Outlook.Items
    Dim obj As Object
    Dim s As String
    Dim n As Long

    Set ns = Application.GetNamespace("MAPI")
    Set itens = ns.GetDefaultFolder(olFolderInbox).Items
    itens.Sort "[ReceivedTime]", True

    For Each obj In itens
        If TypeOf obj Is Outlook.MailItem Then
            If obj.ReceivedTime >= Date - DIAS_VARREDURA Then
                n = n + 1
                s = s & IIf(Mapeado(obj.Subject), "[MAPEADO] ", "[  --   ] ") & _
                        obj.Subject & vbCrLf
                If n >= 60 Then Exit For
            End If
        End If
    Next

    If Len(s) = 0 Then s = "(nenhum e-mail nos ultimos " & DIAS_VARREDURA & " dias)"
    Debug.Print s
    MsgBox s, vbInformation, "Assuntos recentes (veja tambem Ctrl+G)"
End Sub

Private Function Mapeado(sSubject As String) As Boolean
    Dim a As String
    a = LCase$(sSubject & "")
    Mapeado = (InStr(a, LCase$("Report results (FUP-HSA_Salesforce)")) > 0) Or _
              (InStr(a, LCase$("Report results (NB_WON_SF)")) > 0) Or _
              (InStr(a, LCase$("Report results (Funil de Vendas HSA)")) > 0) Or _
              (InStr(a, LCase$("AFM_FUP (Daily)")) > 0) Or _
              (InStr(a, LCase$("AFM_Won (Daily)")) > 0) Or _
              (InStr(a, LCase$("Funil_AFM (Daily)")) > 0)
End Function


' ---------------------------------------------------------------------
'  Utilitarios
' ---------------------------------------------------------------------
Private Function PastaExiste(sPasta As String) As Boolean
    Dim fso As Object
    On Error Resume Next
    Set fso = CreateObject("Scripting.FileSystemObject")
    PastaExiste = fso.FolderExists(sPasta)
End Function

Private Sub Gravar(sTexto As String)
    Dim iFile As Integer

    Debug.Print Format$(Now, "yyyy-mm-dd hh:nn:ss") & " | " & sTexto

    If Len(ARQUIVO_LOG) = 0 Then Exit Sub

    On Error Resume Next
    iFile = FreeFile
    Open ARQUIVO_LOG For Append As #iFile
    If Err.Number = 0 Then
        Print #iFile, Format$(Now, "yyyy-mm-dd hh:nn:ss") & " | " & sTexto
        Close #iFile
    End If
    Err.Clear
End Sub
