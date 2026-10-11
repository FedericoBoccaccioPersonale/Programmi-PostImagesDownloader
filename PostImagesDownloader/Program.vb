Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks

Module Program

    Public ReadOnly Client As New HttpClient()
    Public ReadOnly Errors As New List(Of String)()

    Sub Main(args As String())
        If args.Length <> 1 Then
            Console.WriteLine("Uso:")
            Console.WriteLine()
            Console.WriteLine("  PostImagesDownloader.exe ""URL""")
            Console.WriteLine()
            Console.WriteLine("Postimages:")
            Console.WriteLine("  https://postimg.cc/gallery/ID_GALLERIA")
            Console.WriteLine("  https://postimg.cc/user/NOMEUTENTE")
            Console.WriteLine()
            Console.WriteLine("ImgBB:")
            Console.WriteLine("  https://ibb.co/album/ID_ALBUM")
            Console.WriteLine("  https://ibb.co/album/ID_ALBUM/sub")
            Console.WriteLine("  https://NOMEUTENTE.imgbb.com/albums")
            Console.WriteLine()

            Return
        End If

        Try
            Dim inputUri As Uri = Nothing

            If Not Uri.TryCreate(args(0), UriKind.Absolute, inputUri) Then Throw New ArgumentException("L'URL specificato non è valido.")

            Dim host As String = inputUri.Host.ToLowerInvariant()
            Dim path As String = inputUri.AbsolutePath.Trim("/"c)

            If host = "postimg.cc" OrElse host = "www.postimg.cc" Then
                If path.StartsWith("gallery/", StringComparison.OrdinalIgnoreCase) Then
                    PostImagesDownloader.DownloadGalleryAsync(args(0)).GetAwaiter().GetResult()
                ElseIf path.StartsWith("user/", StringComparison.OrdinalIgnoreCase) Then
                    PostImagesDownloader.DownloadUserAsync(args(0)).GetAwaiter().GetResult()
                Else
                    Throw New ArgumentException("URL Postimages non riconosciuto.")
                End If
            ElseIf host = "ibb.co" OrElse host = "www.ibb.co" Then
                If path.StartsWith("album/", StringComparison.OrdinalIgnoreCase) Then
                    ImgBBDownloader.DownloadAlbumAsync(args(0)).GetAwaiter().GetResult()
                Else
                    Throw New ArgumentException("URL ImgBB non riconosciuto." & Environment.NewLine & "Usa un URL /album/.")
                End If
            ElseIf host.EndsWith(".imgbb.com", StringComparison.OrdinalIgnoreCase) Then
                If path.Equals("albums", StringComparison.OrdinalIgnoreCase) Then
                    ImgBBDownloader.DownloadUserAsync(args(0)).GetAwaiter().GetResult()
                Else
                    Throw New ArgumentException("Per un profilo ImgBB devi utilizzare l'URL /albums.")
                End If
            Else
                Throw New ArgumentException("Dominio non supportato: " & inputUri.Host)
            End If
        Catch ex As Exception
            AddError(ex.Message)
            Environment.ExitCode = 1
        End Try

        PrintErrors()
    End Sub

    Public Sub AddError(message As String)
        If String.IsNullOrWhiteSpace(message) Then message = "È stata richiamata la funzione di errore senza argomento"
        SyncLock Errors
            Errors.Add(message)
        End SyncLock

        Dim previousColor As ConsoleColor = Console.ForegroundColor

        Console.ForegroundColor = ConsoleColor.Red
        Console.WriteLine("ERRORE: " & message)
        Console.ForegroundColor = previousColor
    End Sub

    Public Sub PrintErrors()
        Console.WriteLine()
        Console.WriteLine("==================================================")

        If Errors.Count = 0 Then
            Console.WriteLine("Nessun errore rilevato")
            Return
        End If


        Dim previousColor As ConsoleColor = Console.ForegroundColor
        Console.ForegroundColor = ConsoleColor.Magenta
        Console.WriteLine("LISTA ERRORI")

        For Each errorMessage As String In Errors
            Console.WriteLine("- " & errorMessage)
        Next

        Console.ForegroundColor = previousColor
        Console.WriteLine("==================================================")
    End Sub

    Public Async Function DownloadImageAsync(token As String, originalFileName As String, outputDirectory As String, imageNumber As Integer, downloadedIds As HashSet(Of String), readableImages As List(Of Dictionary(Of String, String))) As Task(Of Integer)
        Console.WriteLine()
        Console.WriteLine("[" & imageNumber & "] " & originalFileName)

        If String.IsNullOrWhiteSpace(originalFileName) Then
            AddError("PostImages: nome del file mancante per l'immagine con token " & token & ".")
            Return 0
        End If

        If downloadedIds.Contains(token) Then
            Console.WriteLine("  Già presente nel registro JSON.")
            Return 1
        End If

        Dim imagePageUrl As String = "https://postimg.cc/" & token
        Dim imageHtml As String

        Try
            imageHtml = Await Client.GetStringAsync(imagePageUrl)
        Catch ex As Exception
            AddError("PostImages: impossibile recuperare l'immagine " & token & ": " & ex.Message)
            Return 0
        End Try

        Dim originalUrl As String = PostImagesDownloader.ExtractOriginalImageUrl(imageHtml)

        If String.IsNullOrWhiteSpace(originalUrl) Then
            AddError("PostImages: URL dell'immagine originale non trovato per il token " & token & ".")
            Return 0
        End If

        Dim imageBytes As Byte()
        Try
            imageBytes = Await Client.GetByteArrayAsync(originalUrl)
        Catch ex As Exception
            AddError("PostImages: download fallito per " & token & ": " & ex.Message)
            Return 0
        End Try

        If imageBytes.Length = 0 Then
            AddError("PostImages: il file scaricato per " & token & " è vuoto.")
            Return 0
        End If

        Dim extension As String = DeterminaEstensione(imageBytes)
        Dim fileName As String = MakeValidFileName(originalFileName)
        If String.IsNullOrWhiteSpace(fileName) Then
            AddError("PostImages: nome del file non valido per il token " & token & ".")
            Return 0
        End If

        Dim baseName As String = Path.GetFileNameWithoutExtension(fileName)
        If String.IsNullOrWhiteSpace(baseName) Then
            AddError("PostImages: nome del file non valido per il token " & token & ".")
            Return 0
        End If

        fileName = baseName & extension

        Dim destination As String = Path.Combine(outputDirectory, fileName)
        destination = GetUniqueFileName(destination)

        Try
            Await File.WriteAllBytesAsync(destination, imageBytes)
        Catch ex As Exception
            AddError("PostImages: impossibile salvare " & fileName & ": " & ex.Message)
            Return 0
        End Try

        downloadedIds.Add(token)
        readableImages.Add(New Dictionary(Of String, String) From {{"id", token}, {"url", originalUrl}, {"originalFileName", originalFileName}, {"localFileName", Path.GetFileName(destination)}})

        Console.WriteLine("  Formato: " & extension)
        Console.WriteLine("  Dimensione: " & FormatFileSize(imageBytes.Length))
        Console.WriteLine("  Salvato: " & Path.GetFileName(destination))

        Return 1
    End Function

    <Obsolete("Non più in uso. Lasciata per evitare di riscriverla nel caso servisse ancora")>
    Public Function FindExistingImage(directory As String, fileName As String) As String
        Dim baseName As String = Path.GetFileNameWithoutExtension(fileName)

        Dim extensions As String() =
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".gif",
            ".bmp",
            ".tif",
            ".tiff",
            ".webp",
            ".avif",
            ".heic",
            ".svg",
            ".bin"
        }

        For Each extension As String In extensions
            Dim candidate As String = Path.Combine(directory, baseName & extension)
            If File.Exists(candidate) Then Return candidate
        Next

        Return String.Empty
    End Function

    Private Function DeterminaEstensione(data As Byte()) As String
        If data.Length >= 3 AndAlso data(0) = &HFF AndAlso data(1) = &HD8 AndAlso data(2) = &HFF Then Return ".jpg"
        If data.Length >= 8 AndAlso data(0) = &H89 AndAlso data(1) = &H50 AndAlso data(2) = &H4E AndAlso data(3) = &H47 AndAlso data(4) = &HD AndAlso data(5) = &HA AndAlso data(6) = &H1A AndAlso data(7) = &HA Then Return ".png"
        If data.Length >= 6 AndAlso data(0) = AscW("G"c) AndAlso data(1) = AscW("I"c) AndAlso data(2) = AscW("F"c) AndAlso data(3) = AscW("8"c) AndAlso (data(4) = AscW("7"c) OrElse data(4) = AscW("9"c)) AndAlso data(5) = AscW("a"c) Then Return ".gif"
        If data.Length >= 2 AndAlso data(0) = AscW("B"c) AndAlso data(1) = AscW("M"c) Then Return ".bmp"
        If data.Length >= 4 AndAlso data(0) = AscW("I"c) AndAlso data(1) = AscW("I"c) AndAlso data(2) = &H2A AndAlso data(3) = 0 Then Return ".tif"
        If data.Length >= 4 AndAlso data(0) = AscW("M"c) AndAlso data(1) = AscW("M"c) AndAlso data(2) = 0 AndAlso data(3) = &H2A Then Return ".tif"
        If data.Length >= 12 AndAlso data(0) = AscW("R"c) AndAlso data(1) = AscW("I"c) AndAlso data(2) = AscW("F"c) AndAlso data(3) = AscW("F"c) AndAlso data(8) = AscW("W"c) AndAlso data(9) = AscW("E"c) AndAlso data(10) = AscW("B"c) AndAlso data(11) = AscW("P"c) Then Return ".webp"

        Dim estensioneIso As String = DeterminaEstensioneIso(data)
        If estensioneIso.Length > 0 Then Return estensioneIso

        If data.Length > 0 Then
            Dim bytesToCheck As Integer = Math.Min(data.Length, 1024)

            Dim header As String = System.Text.Encoding.UTF8.GetString(data, 0, bytesToCheck)

            If Regex.IsMatch(header, "<\s*svg\b", RegexOptions.IgnoreCase) Then Return ".svg"
        End If

        Return ".bin"
    End Function

    ''' <summary>
    ''' Non testata
    ''' </summary>
    ''' <param name="dati"></param>
    ''' <param name="posizione"></param>
    ''' <returns></returns>
    Private Function LeggiMarchioIso(dati As Byte(), posizione As Integer) As String
        Return System.Text.Encoding.ASCII.GetString(dati, posizione, 4)
    End Function
    ''' <summary>
    ''' Non testata
    ''' </summary>
    ''' <param name="dati"></param>
    ''' <returns></returns>
    Private Function DeterminaEstensioneIso(dati As Byte()) As String
        If dati.Length < 16 Then Return String.Empty
        If LeggiMarchioIso(dati, 4) <> "ftyp" Then Return String.Empty

        Dim dimensione As Long = (CLng(dati(0)) << 24) Or (CLng(dati(1)) << 16) Or (CLng(dati(2)) << 8) Or CLng(dati(3))

        If dimensione < 16 Then dimensione = 16
        Dim fine As Integer = CInt(Math.Min(dimensione, CLng(dati.Length)))
        Dim marchi As New List(Of String)()

        marchi.Add(LeggiMarchioIso(dati, 8))
        Dim posizione As Integer = 16

        While posizione + 4 <= fine
            marchi.Add(LeggiMarchioIso(dati, posizione))
            posizione += 4
        End While

        If marchi.Contains("avif") OrElse marchi.Contains("avis") Then Return ".avif"

        For Each marchio As String In marchi
            If marchio = "heic" OrElse marchio = "heix" OrElse marchio = "hevc" OrElse marchio = "hevx" OrElse marchio = "heim" OrElse marchio = "heis" OrElse marchio = "mif1" OrElse marchio = "msf1" Then Return ".heic"
        Next

        Return String.Empty
    End Function

    <Obsolete("Non più in uso. Lasciata per evitare di riscriverla nel caso servisse ancora")>
    Public Function ContainsAscii(data As Byte(), value As String) As Boolean
        If data.Length = 0 Then Return False
        Dim searchBytes As Byte() = System.Text.Encoding.ASCII.GetBytes(value)

        If searchBytes.Length = 0 OrElse searchBytes.Length > data.Length Then Return False
        For i As Integer = 0 To data.Length - searchBytes.Length
            Dim found As Boolean = True

            For j As Integer = 0 To searchBytes.Length - 1
                If data(i + j) <> searchBytes(j) Then
                    found = False
                    Exit For
                End If
            Next

            If found Then Return True
        Next

        Return False
    End Function

    Public Function GetJsonString(values As List(Of JsonElement), index As Integer) As String
        If index < 0 OrElse index >= values.Count Then Return String.Empty
        Dim value As JsonElement = values(index)

        If value.ValueKind = JsonValueKind.String Then Return value.GetString()
        Return value.ToString()
    End Function

    Public Function MakeValidDirectoryName(name As String) As String
        If name Is Nothing Then Return "PostImages_Gallery"
        For Each invalidChar As Char In Path.GetInvalidFileNameChars()
            name = name.Replace(invalidChar, "_"c)
        Next

        name = name.Trim()

        If String.IsNullOrWhiteSpace(name) Then name = "PostImages_Gallery"
        Return name
    End Function

    Public Function MakeValidFileName(name As String) As String
        If name Is Nothing Then Return String.Empty
        For Each invalidChar As Char In Path.GetInvalidFileNameChars()
            name = name.Replace(invalidChar, "_"c)
        Next

        Return name.Trim()
    End Function

    Public Function GetUniqueFileName(path As String) As String
        If Not File.Exists(path) Then Return path
        Dim directory As String = IO.Path.GetDirectoryName(path)
        Dim name As String = IO.Path.GetFileNameWithoutExtension(path)
        Dim extension As String = IO.Path.GetExtension(path)
        Dim number As Integer = 2

        Do
            Dim candidate As String = IO.Path.Combine(directory, name & "_" & number & extension)

            If Not File.Exists(candidate) Then Return candidate
            number += 1
        Loop
    End Function

    Public Function FormatFileSize(size As Long) As String
        If size < 1024 Then Return size & " B"
        If size < 1024L * 1024L Then Return Math.Round(size / 1024.0, 1) & " KB"
        Return Math.Round(size / (1024.0 * 1024.0), 1) & " MB"
    End Function

    Public Async Function DownloadDirectImageAsync(imageUrl As String, originalFileName As String, outputDirectory As String, imageNumber As Integer, imageId As String, downloadedIds As HashSet(Of String), readableImages As List(Of Dictionary(Of String, String))) As Task(Of Integer)
        If String.IsNullOrWhiteSpace(originalFileName) Then
            AddError("ImgBB: nome del file mancante per l'immagine " & imageId & ".")
            Return 0
        End If

        If downloadedIds.Contains(imageId) Then
            Console.WriteLine("[" & imageNumber & "] Già presente nel registro JSON: " & imageId)
            Return 1
        End If

        Console.WriteLine("[" & imageNumber & "] " & originalFileName)

        Dim imageBytes As Byte()

        Try
            imageBytes = Await Client.GetByteArrayAsync(imageUrl)
        Catch ex As Exception
            AddError("ImgBB: download fallito per " & imageId & ": " & ex.Message)
            Return 0
        End Try

        If imageBytes.Length = 0 Then
            AddError("ImgBB: file vuoto per " & imageId & ".")
            Return 0
        End If

        Dim extension As String = DeterminaEstensione(imageBytes)
        Dim fileName As String = MakeValidFileName(originalFileName)

        If String.IsNullOrWhiteSpace(fileName) Then
            AddError("ImgBB: nome del file non valido per " & imageId & ".")
            Return 0
        End If

        Dim baseName As String = Path.GetFileNameWithoutExtension(fileName)

        If String.IsNullOrWhiteSpace(baseName) Then
            AddError("ImgBB: nome del file non valido per " & imageId & ".")
            Return 0
        End If

        fileName = baseName & extension

        Dim destination As String = Path.Combine(outputDirectory, fileName)
        destination = GetUniqueFileName(destination)

        Try
            Await File.WriteAllBytesAsync(destination, imageBytes)
        Catch ex As Exception
            AddError("ImgBB: impossibile salvare " & fileName & ": " & ex.Message)
            Return 0
        End Try

        downloadedIds.Add(imageId)

        readableImages.Add(New Dictionary(Of String, String) From {{"id", imageId}, {"url", imageUrl}, {"originalFileName", originalFileName}, {"localFileName", Path.GetFileName(destination)}})

        Console.WriteLine("  Formato: " & extension)
        Console.WriteLine("  Dimensione: " & FormatFileSize(imageBytes.Length))
        Console.WriteLine("  Salvato: " & Path.GetFileName(destination))

        Return 1
    End Function
End Module
