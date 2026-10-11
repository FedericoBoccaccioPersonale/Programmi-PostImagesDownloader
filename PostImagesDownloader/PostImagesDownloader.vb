Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.RegularExpressions

Public Module PostImagesDownloader
    Public Async Function DownloadGalleryAsync(galleryUrl As String, Optional parentDirectory As String = Nothing) As Task
        Dim galleryUri As Uri = Nothing

        If Not Uri.TryCreate(galleryUrl, UriKind.Absolute, galleryUri) Then Throw New ArgumentException("L'URL specificato non è valido.")
        If Not String.Equals(galleryUri.Host, "postimg.cc", StringComparison.OrdinalIgnoreCase) AndAlso Not String.Equals(galleryUri.Host, "www.postimg.cc", StringComparison.OrdinalIgnoreCase) Then Throw New ArgumentException("L'URL deve appartenere a postimg.cc.")
        Dim match As Match = Regex.Match(galleryUri.AbsolutePath, "^/gallery/([^/]+)", RegexOptions.IgnoreCase)

        If Not match.Success Then Throw New ArgumentException("L'URL non sembra essere quello di una galleria Postimages." & Environment.NewLine & "Esempio:" & Environment.NewLine & "https://postimg.cc/gallery/4dw5KCx")
        Dim albumId As String = match.Groups(1).Value

        Console.WriteLine("Galleria: " & albumId)
        Console.WriteLine("Recupero informazioni della galleria...")

        Dim galleryPageUrl As String = "https://postimg.cc/gallery/" & albumId

        Dim galleryHtml As String

        Try
            galleryHtml = Await Program.Client.GetStringAsync(galleryPageUrl)
        Catch ex As Exception
            Program.AddError("PostImages: impossibile recuperare la galleria " & albumId & ": " & ex.Message)
            Return
        End Try

        Dim galleryTitle As String = ExtractGalleryTitle(galleryHtml)

        If String.IsNullOrWhiteSpace(galleryTitle) Then galleryTitle = albumId
        galleryTitle = Program.MakeValidDirectoryName(galleryTitle)

        Dim baseDirectory As String

        If String.IsNullOrWhiteSpace(parentDirectory) Then
            baseDirectory = Environment.CurrentDirectory
        Else
            baseDirectory = parentDirectory
        End If

        Dim outputDirectory As String = Path.Combine(baseDirectory, galleryTitle)

        Directory.CreateDirectory(outputDirectory)

        Console.WriteLine("Nome galleria: " & galleryTitle)
        Console.WriteLine("Cartella: " & outputDirectory)
        Console.WriteLine()

        Dim readablePath As String = Path.Combine(outputDirectory, "gallery-readable.json")

        Dim downloadedIds As HashSet(Of String) = LoadDownloadedIds(readablePath)

        Dim readableImages As New List(Of Dictionary(Of String, String))()
        Dim rawPages As New List(Of JsonElement)()
        Dim page As Integer = 1
        Dim totalDownloaded As Integer = 0

        Do
            Console.WriteLine("Recupero pagina " & page & "...")

            Dim json As String

            Try
                json = Await GetGalleryPageAsync(albumId, page)
            Catch ex As Exception
                Program.AddError("PostImages: impossibile recuperare la pagina " & page & " della galleria " & albumId & ": " & ex.Message)
                Exit Do
            End Try

            Try
                Using document As JsonDocument = JsonDocument.Parse(json)
                    rawPages.Add(document.RootElement.Clone())

                    Dim root As JsonElement = document.RootElement

                    Dim imagesProperty As JsonElement

                    If Not root.TryGetProperty("images", imagesProperty) OrElse imagesProperty.ValueKind <> JsonValueKind.Array Then Exit Do
                    Dim imagesOnPage As Integer = 0

                    For Each image As JsonElement In imagesProperty.EnumerateArray()
                        If image.ValueKind <> JsonValueKind.Array Then Continue For
                        Dim values As New List(Of JsonElement)()

                        For Each value As JsonElement In image.EnumerateArray()
                            values.Add(value)
                        Next

                        If values.Count < 3 Then
                            Program.AddError("PostImages: elemento immagine incompleto nella pagina " & page & " della galleria " & albumId & ".")
                            Continue For
                        End If

                        Dim token As String = Program.GetJsonString(values, 0)

                        If String.IsNullOrWhiteSpace(token) Then
                            Program.AddError("PostImages: identificatore immagine mancante nella pagina " & page & " della galleria " & albumId & ".")
                            Continue For
                        End If

                        Dim fileName As String = Program.GetJsonString(values, 2)

                        If String.IsNullOrWhiteSpace(fileName) Then
                            Program.AddError("PostImages: nome file mancante per l'immagine " & token & ".")
                            Continue For
                        End If

                        imagesOnPage += 1

                        totalDownloaded += Await Program.DownloadImageAsync(token, fileName, outputDirectory, totalDownloaded + 1, downloadedIds, readableImages)
                    Next

                    Dim hasNext As Boolean = False
                    Dim hasPageNext As JsonElement

                    If root.TryGetProperty("has_page_next", hasPageNext) AndAlso (hasPageNext.ValueKind = JsonValueKind.True OrElse hasPageNext.ValueKind = JsonValueKind.False) Then hasNext = hasPageNext.GetBoolean()
                    If imagesOnPage = 0 OrElse Not hasNext Then Exit Do
                End Using
            Catch ex As Exception
                Program.AddError("PostImages: JSON non valido nella pagina " & page & " della galleria " & albumId & ": " & ex.Message)
                Exit Do
            End Try

            page += 1
        Loop

        SaveJsonFiles(outputDirectory, albumId, galleryTitle, rawPages, readableImages)

        Console.WriteLine()
        Console.WriteLine("Completato.")
        Console.WriteLine("Immagini scaricate o già presenti: " & totalDownloaded)
        Console.WriteLine("Cartella: " & outputDirectory)
    End Function

    Public Async Function GetGalleryPageAsync(albumId As String, page As Integer) As Task(Of String)
        Dim url As String = "https://postimg.cc/json"

        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = New FormUrlEncodedContent(New Dictionary(Of String, String) From {{"action", "list"}, {"album", albumId}, {"page", page.ToString()}})

            request.Headers.Referrer = New Uri("https://postimg.cc/gallery/" & albumId)

            Using response As HttpResponseMessage = Await Program.Client.SendAsync(request)
                response.EnsureSuccessStatusCode()

                Return Await response.Content.ReadAsStringAsync()
            End Using
        End Using
    End Function

    Public Function ExtractGalleryTitle(html As String) As String
        Dim match As Match = Regex.Match(html, "<meta\s+property=""og:title""\s+content=""([^""]*)""", RegexOptions.IgnoreCase)

        If match.Success Then
            Dim title As String = WebUtility.HtmlDecode(match.Groups(1).Value)
            title = Regex.Replace(title, "\s+[—-]\s+Postimages.*$", "", RegexOptions.IgnoreCase)
            Return title.Trim()
        End If

        Return String.Empty
    End Function

    Public Function ExtractOriginalImageUrl(html As String) As String
        Dim downloadPosition As Integer = html.IndexOf(" id=""download""", StringComparison.OrdinalIgnoreCase)

        If downloadPosition < 0 Then Return String.Empty
        Dim hrefStart As Integer = html.LastIndexOf(" href=""", downloadPosition, StringComparison.OrdinalIgnoreCase)

        If hrefStart < 0 Then Return String.Empty
        hrefStart += 7

        Dim hrefEnd As Integer = html.IndexOf(""""c, hrefStart)

        If hrefEnd < 0 Then Return String.Empty
        Dim url As String = html.Substring(hrefStart, hrefEnd - hrefStart)

        url = WebUtility.HtmlDecode(url).Trim()

        Dim validUri As Uri = Nothing

        If Not Uri.TryCreate(url, UriKind.Absolute, validUri) Then Return String.Empty
        If validUri.Scheme <> Uri.UriSchemeHttp AndAlso validUri.Scheme <> Uri.UriSchemeHttps Then Return String.Empty
        Return url
    End Function

    Public Async Function DownloadUserAsync(userUrl As String) As Task
        Dim userUri As Uri = Nothing

        If Not Uri.TryCreate(userUrl, UriKind.Absolute, userUri) Then Throw New ArgumentException("URL del profilo Postimages non valido.")
        Dim userMatch As Match = Regex.Match(userUri.AbsolutePath, "^/user/([^/]+)", RegexOptions.IgnoreCase)

        If Not userMatch.Success Then Throw New ArgumentException("URL del profilo Postimages non riconosciuto.")
        Dim username As String = userMatch.Groups(1).Value
        Dim NomeCartella As String = Program.MakeValidDirectoryName(WebUtility.UrlDecode(username))

        Dim userDirectory As String = Path.Combine(Environment.CurrentDirectory, NomeCartella)

        Directory.CreateDirectory(userDirectory)

        Console.WriteLine("Profilo utente: " & username)
        Console.WriteLine()
        Console.WriteLine("Recupero gallerie pubbliche...")

        Dim html As String
        Try
            html = Await Program.Client.GetStringAsync(userUrl)
        Catch ex As Exception
            Program.AddError("PostImages: impossibile recuperare il profilo " & username & ": " & ex.Message)
            Return
        End Try

        Dim galleryUrls As New List(Of String)()
        Dim pattern As String = "https?://(?:www\.)?postimg\.cc/gallery/[^""'<>?\s#]+"

        For Each match As Match In Regex.Matches(html, pattern, RegexOptions.IgnoreCase)
            Dim galleryUrl As String = match.Value.TrimEnd("/"c, "."c, ","c, ";"c, ")"c)

            If Not galleryUrls.Contains(galleryUrl, StringComparer.OrdinalIgnoreCase) Then galleryUrls.Add(galleryUrl)
        Next

        Dim readableAlbums As New List(Of Dictionary(Of String, String))()

        For Each galleryUrl As String In galleryUrls
            readableAlbums.Add(New Dictionary(Of String, String) From {{"url", galleryUrl}})
        Next

        SaveUserJson(userDirectory, NomeCartella, galleryUrls, readableAlbums)

        Console.WriteLine("Gallerie trovate: " & galleryUrls.Count)
        Console.WriteLine()

        If galleryUrls.Count = 0 Then
            Console.WriteLine("Nessuna galleria pubblica trovata.")
            Return
        End If

        For i As Integer = 0 To galleryUrls.Count - 1
            Console.WriteLine("==================================================")
            Console.WriteLine("GALLERIA " & (i + 1) & "/" & galleryUrls.Count)
            Console.WriteLine(galleryUrls(i))
            Console.WriteLine("==================================================")
            Console.WriteLine()

            Try
                Await DownloadGalleryAsync(galleryUrls(i), userDirectory)
            Catch ex As Exception
                Program.AddError("PostImages: errore nella galleria " & galleryUrls(i) & ": " & ex.Message)
                Console.WriteLine("Continuo con la galleria successiva.")
            End Try
        Next

        Console.WriteLine()
        Console.WriteLine("==================================================")
        Console.WriteLine("PROFILO COMPLETATO")
        Console.WriteLine("Gallerie elaborate: " & galleryUrls.Count)
        Console.WriteLine("==================================================")
    End Function

    Private Function LoadDownloadedIds(path As String) As HashSet(Of String)
        Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        If Not File.Exists(path) Then Return result
        Try
            Using document As JsonDocument = JsonDocument.Parse(File.ReadAllText(path))
                Dim images As JsonElement

                If document.RootElement.TryGetProperty("images", images) AndAlso images.ValueKind = JsonValueKind.Array Then
                    For Each image As JsonElement In images.EnumerateArray()
                        Dim id As JsonElement
                        If image.TryGetProperty("id", id) AndAlso id.ValueKind = JsonValueKind.String Then result.Add(id.GetString())
                    Next
                End If
            End Using
        Catch ex As Exception
            Program.AddError("PostImages: impossibile leggere il registro " & path & ": " & ex.Message)
        End Try

        Return result
    End Function

    Private Sub SaveJsonFiles(directory As String, albumId As String, title As String, rawPages As List(Of JsonElement), images As List(Of Dictionary(Of String, String)))
        Dim options As New JsonSerializerOptions With {.WriteIndented = True}

        Dim rawPath As String = Path.Combine(directory, "gallery.json")

        Dim readablePath As String = Path.Combine(directory, "gallery-readable.json")

        Try
            File.WriteAllText(rawPath, JsonSerializer.Serialize(rawPages, options))

            File.WriteAllText(readablePath, JsonSerializer.Serialize(New With {.site = "PostImages", .galleryId = albumId, .title = title, .images = images}, options))
        Catch ex As Exception
            Program.AddError("PostImages: impossibile salvare i JSON della galleria " & albumId & ": " & ex.Message)
        End Try
    End Sub

    Private Sub SaveUserJson(directory As String, username As String, galleryUrls As List(Of String), albums As List(Of Dictionary(Of String, String)))
        Dim options As New JsonSerializerOptions With {.WriteIndented = True}

        Try
            File.WriteAllText(Path.Combine(directory, "galleries.json"), JsonSerializer.Serialize(galleryUrls, options))

            File.WriteAllText(Path.Combine(directory, "galleries-readable.json"), JsonSerializer.Serialize(New With {.site = "PostImages", .username = username, .galleries = albums}, options))
        Catch ex As Exception
            Program.AddError("PostImages: impossibile salvare il JSON delle gallerie dell'utente " & username & ": " & ex.Message)
        End Try
    End Sub
End Module
