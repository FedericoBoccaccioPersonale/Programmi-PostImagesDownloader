
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks

Public Module ImgBBDownloader

    Private Class ImgBBPageInfo
        Public Property AuthToken As String = String.Empty
        Public Property JsonApi As String = "https://ibb.co/json"
        Public Property ItemsPerPage As Integer = 32
    End Class

    Private Class AlbumInfo
        Public Property Id As String = String.Empty
        Public Property Name As String = String.Empty
        Public Property Url As String = String.Empty
        Public Property LocalDirectory As String = String.Empty
    End Class

    Public Async Function DownloadAlbumAsync(albumUrl As String, Optional parentDirectory As String = Nothing) As Task(Of String)
        Dim albumUri As Uri = Nothing
        If Not Uri.TryCreate(albumUrl, UriKind.Absolute, albumUri) Then Throw New ArgumentException("URL ImgBB non valido.")
        Dim albumPath As String = albumUri.AbsolutePath.TrimEnd("/"c)
        Dim albumId As String = ExtractAlbumId(albumPath)
        If String.IsNullOrWhiteSpace(albumId) Then Throw New ArgumentException("Impossibile determinare l'ID dell'album.")

        Dim albumPage As ImgBBPageInfo = Await GetPageInfoAsync(albumUrl)
        If String.IsNullOrWhiteSpace(albumPage.AuthToken) Then Throw New InvalidOperationException("ImgBB non ha fornito l'auth_token.")

        Dim albumHtml As String
        Try
            albumHtml = Await Program.Client.GetStringAsync(albumUrl)
        Catch ex As Exception
            Program.AddError("ImgBB: impossibile recuperare l'album " & albumId & ": " & ex.Message)
            Return String.Empty
        End Try

        Dim albumTitle As String = ExtractTitle(albumHtml)
        If String.IsNullOrWhiteSpace(albumTitle) Then albumTitle = albumId
        albumTitle = Program.MakeValidDirectoryName(albumTitle)

        Dim baseDirectory As String = If(String.IsNullOrWhiteSpace(parentDirectory), Environment.CurrentDirectory, parentDirectory)
        Dim outputDirectory As String = GetAlbumDirectory(baseDirectory, albumTitle, albumId)
        Directory.CreateDirectory(outputDirectory)

        Console.WriteLine("Album ImgBB: " & albumTitle)
        Console.WriteLine("Cartella: " & outputDirectory)
        Console.WriteLine()

        Dim readablePath As String = Path.Combine(outputDirectory, "album-readable.json")
        Dim readableImages As List(Of Dictionary(Of String, String)) = LoadDownloadedImages(readablePath)
        Dim downloadedIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each image As Dictionary(Of String, String) In readableImages
            Dim imageId As String = Nothing
            If image.TryGetValue("id", imageId) AndAlso Not String.IsNullOrWhiteSpace(imageId) Then downloadedIds.Add(imageId)
        Next

        Dim readableSubAlbums As New List(Of Dictionary(Of String, String))()
        Dim rawPages As New List(Of JsonElement)()
        Dim page As Integer = 1
        Dim seek As String = String.Empty
        Dim total As Integer = 0

        Do
            Dim json As String
            Try
                json = Await GetListAsync(albumPage, page, seek, albumPath, "images")
            Catch ex As Exception
                Program.AddError("ImgBB: impossibile recuperare la pagina " & page & " dell'album " & albumId & ": " & ex.Message)
                Exit Do
            End Try

            Try
                Using document As JsonDocument = JsonDocument.Parse(json)
                    rawPages.Add(document.RootElement.Clone())
                    Dim objects As List(Of JsonElement) = ExtractObjects(json)

                    For Each imageObject As JsonElement In objects
                        Dim imageId As String = GetImageIdentifier(imageObject)
                        If String.IsNullOrWhiteSpace(imageId) Then
                            Program.AddError("ImgBB: identificatore mancante per un'immagine dell'album " & albumId & ".")
                            Continue For
                        End If

                        Dim imageUrl As String = GetNestedObjectString(imageObject, "image", "url")
                        If String.IsNullOrWhiteSpace(imageUrl) Then imageUrl = GetObjectString(imageObject, "url")
                        If String.IsNullOrWhiteSpace(imageUrl) Then
                            Program.AddError("ImgBB: URL mancante per l'immagine " & imageId & ".")
                            Continue For
                        End If

                        Dim fileName As String = GetNestedObjectString(imageObject, "image", "filename")
                        If String.IsNullOrWhiteSpace(fileName) Then fileName = GetObjectString(imageObject, "filename")
                        If String.IsNullOrWhiteSpace(fileName) Then
                            Program.AddError("ImgBB: nome del file mancante per l'immagine " & imageId & ".")
                            Continue For
                        End If

                        total += Await Program.DownloadDirectImageAsync(imageUrl, fileName, outputDirectory, total + 1, imageId, downloadedIds, readableImages)
                    Next
                End Using
            Catch ex As Exception
                Program.AddError("ImgBB: JSON non valido per l'album " & albumId & ": " & ex.Message)
                Exit Do
            End Try

            Dim newSeek As String = ExtractSeek(json)
            If String.IsNullOrWhiteSpace(newSeek) OrElse String.Equals(newSeek, seek, StringComparison.Ordinal) Then Exit Do
            seek = newSeek
            page += 1
        Loop

        Dim subAlbums As List(Of AlbumInfo) = Await GetSubAlbumsAsync(albumPath, albumPage)
        For Each subAlbum As AlbumInfo In subAlbums
            Dim subDirectory As String = Await DownloadAlbumAsync(subAlbum.Url, outputDirectory)
            If Not String.IsNullOrWhiteSpace(subDirectory) Then readableSubAlbums.Add(New Dictionary(Of String, String) From {{"id", subAlbum.Id}, {"name", subAlbum.Name}, {"url", subAlbum.Url}, {"localDirectory", Path.GetFileName(subDirectory)}})
        Next

        SaveAlbumJson(outputDirectory, albumId, albumTitle, rawPages, readableImages, readableSubAlbums)
        Console.WriteLine()
        Console.WriteLine("Album completato.")
        Return outputDirectory
    End Function

    Public Async Function DownloadUserAsync(userUrl As String) As Task
        Dim userUri As Uri = Nothing
        If Not Uri.TryCreate(userUrl, UriKind.Absolute, userUri) Then Throw New ArgumentException("URL del profilo ImgBB non valido.")

        Dim html As String
        Try
            html = Await Program.Client.GetStringAsync(userUrl)
        Catch ex As Exception
            Program.AddError("ImgBB: impossibile recuperare il profilo " & userUri.Host & ": " & ex.Message)
            Return
        End Try

        Dim pageInfo As ImgBBPageInfo = ParsePageInfo(html)
        If String.IsNullOrWhiteSpace(pageInfo.AuthToken) Then Throw New InvalidOperationException("ImgBB non ha fornito l'auth_token.")

        Dim username As String = userUri.Host
        If username.EndsWith(".imgbb.com", StringComparison.OrdinalIgnoreCase) Then username = username.Substring(0, username.Length - ".imgbb.com".Length)
        username = Program.MakeValidDirectoryName(username)

        Dim outputDirectory As String = Path.Combine(Environment.CurrentDirectory, username)
        Directory.CreateDirectory(outputDirectory)

        Console.WriteLine("Utente ImgBB: " & username)
        Console.WriteLine()
        Console.WriteLine("Recupero album...")

        Dim albums As New List(Of AlbumInfo)()
        Dim rawPages As New List(Of JsonElement)()
        Dim page As Integer = 1
        Dim seek As String = String.Empty

        Do
            Dim json As String
            Try
                json = Await GetListAsync(pageInfo, page, seek, "/", "albums")
            Catch ex As Exception
                Program.AddError("ImgBB: impossibile recuperare la pagina " & page & " degli album di " & username & ": " & ex.Message)
                Exit Do
            End Try

            Try
                Using document As JsonDocument = JsonDocument.Parse(json)
                    rawPages.Add(document.RootElement.Clone())
                End Using

                Dim objects As List(Of JsonElement) = ExtractObjects(json)
                If objects.Count = 0 Then Exit Do

                For Each albumObject As JsonElement In objects
                    Dim albumId As String = GetObjectString(albumObject, "id")
                    If String.IsNullOrWhiteSpace(albumId) Then
                        Program.AddError("ImgBB: ID mancante per un album del profilo " & username & ".")
                        Continue For
                    End If

                    Dim albumUrl As String = GetObjectString(albumObject, "url")
                    If String.IsNullOrWhiteSpace(albumUrl) Then
                        Program.AddError("ImgBB: URL mancante per l'album " & albumId & ".")
                        Continue For
                    End If

                    Dim albumName As String = GetObjectString(albumObject, "name")
                    If String.IsNullOrWhiteSpace(albumName) Then albumName = GetObjectString(albumObject, "title")
                    If String.IsNullOrWhiteSpace(albumName) Then
                        Program.AddError("ImgBB: nome dell'album mancante per " & albumId & ".")
                        Continue For
                    End If

                    If Not albums.Exists(Function(item As AlbumInfo) String.Equals(item.Id, albumId, StringComparison.OrdinalIgnoreCase)) Then albums.Add(New AlbumInfo With {.Id = albumId, .Name = albumName, .Url = albumUrl})
                Next
            Catch ex As Exception
                Program.AddError("ImgBB: JSON non valido nell'elenco album di " & username & ": " & ex.Message)
                Exit Do
            End Try

            Dim newSeek As String = ExtractSeek(json)
            If String.IsNullOrWhiteSpace(newSeek) OrElse String.Equals(newSeek, seek, StringComparison.Ordinal) Then Exit Do
            seek = newSeek
            page += 1
        Loop

        Console.WriteLine("Album trovati: " & albums.Count)
        Console.WriteLine()

        Dim readableAlbums As New List(Of Dictionary(Of String, String))()
        For i As Integer = 0 To albums.Count - 1
            Console.WriteLine("==================================================")
            Console.WriteLine("ALBUM " & (i + 1) & "/" & albums.Count)
            Console.WriteLine(albums(i).Name)
            Console.WriteLine(albums(i).Url)
            Console.WriteLine("==================================================")
            Console.WriteLine()

            Try
                Dim localDirectory As String = Await DownloadAlbumAsync(albums(i).Url, outputDirectory)
                If Not String.IsNullOrWhiteSpace(localDirectory) Then readableAlbums.Add(New Dictionary(Of String, String) From {{"id", albums(i).Id}, {"name", albums(i).Name}, {"url", albums(i).Url}, {"localDirectory", Path.GetFileName(localDirectory)}})
            Catch ex As Exception
                Program.AddError("ImgBB: errore nell'album " & albums(i).Name & ": " & ex.Message)
            End Try
        Next

        SaveUserJson(outputDirectory, username, rawPages, readableAlbums)
    End Function

    Private Async Function GetSubAlbumsAsync(pathname As String, pageInfo As ImgBBPageInfo) As Task(Of List(Of AlbumInfo))
        Dim result As New List(Of AlbumInfo)()
        Dim page As Integer = 1
        Dim seek As String = String.Empty

        Do
            Dim json As String
            Try
                json = Await GetListAsync(pageInfo, page, seek, pathname, "albums")
            Catch ex As Exception
                Program.AddError("ImgBB: impossibile recuperare i sottoalbum di " & pathname & " (potrebbero non essercene): " & ex.Message)
                Exit Do
            End Try

            Dim objects As List(Of JsonElement)
            ' Non ho mai provato i sottoalbum, non ho testato la parte che segue
            Try
                objects = ExtractObjects(json)
            Catch ex As Exception
                Program.AddError("ImgBB: risposta JSON non valida per i sottoalbum di " & pathname & ": " & ex.Message)
                Exit Do
            End Try

            For Each albumObject As JsonElement In objects
                Dim albumId As String = GetObjectString(albumObject, "id")
                Dim albumUrl As String = GetObjectString(albumObject, "url")
                Dim albumName As String = GetObjectString(albumObject, "name")
                If String.IsNullOrWhiteSpace(albumName) Then albumName = GetObjectString(albumObject, "title")
                If String.IsNullOrWhiteSpace(albumId) OrElse String.IsNullOrWhiteSpace(albumUrl) OrElse String.IsNullOrWhiteSpace(albumName) Then Continue For
                If Not result.Exists(Function(item As AlbumInfo) String.Equals(item.Id, albumId, StringComparison.OrdinalIgnoreCase)) Then result.Add(New AlbumInfo With {.Id = albumId, .Name = albumName, .Url = albumUrl})
            Next

            Dim newSeek As String = ExtractSeek(json)
            If String.IsNullOrWhiteSpace(newSeek) OrElse String.Equals(newSeek, seek, StringComparison.Ordinal) Then Exit Do
            seek = newSeek
            page += 1
        Loop

        Return result
    End Function

    Private Async Function GetPageInfoAsync(url As String) As Task(Of ImgBBPageInfo)
        Dim html As String = Await Program.Client.GetStringAsync(url)
        Return ParsePageInfo(html)
    End Function

    Private Function ParsePageInfo(html As String) As ImgBBPageInfo
        Dim result As New ImgBBPageInfo()
        Dim match As Match = Regex.Match(html, "PF\.obj\.config\.auth_token\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)
        If match.Success Then result.AuthToken = match.Groups(1).Value
        match = Regex.Match(html, "PF\.obj\.config\.json_api\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)
        If match.Success Then result.JsonApi = match.Groups(1).Value
        match = Regex.Match(html, "PF\.obj\.config\.listing\.items_per_page\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)
        If match.Success Then Integer.TryParse(match.Groups(1).Value, result.ItemsPerPage)
        Return result
    End Function

    Private Async Function GetListAsync(pageInfo As ImgBBPageInfo, page As Integer, seek As String, pathname As String, Optional list As String = "images") As Task(Of String)
        Using request As New HttpRequestMessage(HttpMethod.Post, pageInfo.JsonApi)
            request.Content = New FormUrlEncodedContent(New Dictionary(Of String, String) From {{"action", "list"}, {"list", list}, {"page", page.ToString()}, {"seek", seek}, {"auth_token", pageInfo.AuthToken}, {"pathname", pathname}})
            request.Headers.Referrer = New Uri("https://ibb.co" & pathname)
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest")
            Using response As HttpResponseMessage = Await Program.Client.SendAsync(request)
                response.EnsureSuccessStatusCode()
                Return Await response.Content.ReadAsStringAsync()
            End Using
        End Using
    End Function

    Private Function ExtractObjects(json As String) As List(Of JsonElement)
        Dim result As New List(Of JsonElement)()
        If String.IsNullOrWhiteSpace(json) Then Return result

        Using document As JsonDocument = JsonDocument.Parse(json)
            Dim root As JsonElement = document.RootElement
            Dim html As String = String.Empty
            Dim htmlProperty As JsonElement
            If root.TryGetProperty("html", htmlProperty) AndAlso htmlProperty.ValueKind = JsonValueKind.String Then html = htmlProperty.GetString()
            If String.IsNullOrWhiteSpace(html) Then Return result

            For Each match As Match In Regex.Matches(html, "data-object\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                Dim encoded As String = match.Groups(2).Value
                Dim decoded As String = WebUtility.HtmlDecode(WebUtility.UrlDecode(encoded))
                Try
                    Using objectDocument As JsonDocument = JsonDocument.Parse(decoded)
                        result.Add(objectDocument.RootElement.Clone())
                    End Using
                Catch ex As JsonException
                    Program.AddError("ImgBB: impossibile interpretare un oggetto immagine/album: " & ex.Message)
                End Try
            Next

            For Each match As Match In Regex.Matches(html, "<div[^>]*data-type\s*=\s*(['""])album\1[^>]*>", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                Dim tag As String = match.Value
                Dim idMatch As Match = Regex.Match(tag, "data-id\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                Dim nameMatch As Match = Regex.Match(tag, "data-name\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
                If Not idMatch.Success Then Continue For

                Dim albumId As String = WebUtility.HtmlDecode(idMatch.Groups(2).Value)
                Dim albumName As String = If(nameMatch.Success, WebUtility.HtmlDecode(nameMatch.Groups(2).Value), String.Empty)
                Dim albumJson As String = JsonSerializer.Serialize(New With {.id = albumId, .name = albumName, .url = "https://ibb.co/album/" & albumId})

                Using objectDocument As JsonDocument = JsonDocument.Parse(albumJson)
                    result.Add(objectDocument.RootElement.Clone())
                End Using
            Next
        End Using

        Return result
    End Function

    Private Function ExtractSeek(json As String) As String
        Using document As JsonDocument = JsonDocument.Parse(json)
            Dim seek As JsonElement
            If document.RootElement.TryGetProperty("seekEnd", seek) Then Return seek.ToString()
        End Using
        Return String.Empty
    End Function

    Private Function GetObjectString(element As JsonElement, propertyName As String) As String
        Dim value As JsonElement
        If element.ValueKind = JsonValueKind.Object AndAlso element.TryGetProperty(propertyName, value) AndAlso value.ValueKind = JsonValueKind.String Then Return value.GetString()
        Return String.Empty
    End Function

    Private Function GetNestedObjectString(element As JsonElement, objectName As String, propertyName As String) As String
        Dim nested As JsonElement
        If Not element.TryGetProperty(objectName, nested) Then Return String.Empty
        Return GetObjectString(nested, propertyName)
    End Function

    Private Function GetImageIdentifier(element As JsonElement) As String
        Dim value As String = GetObjectString(element, "id_encoded")
        If Not String.IsNullOrWhiteSpace(value) Then Return value
        ' Non ho mai avuto occasione di eseguire la parte che segue
        value = GetObjectString(element, "id")
        If Not String.IsNullOrWhiteSpace(value) Then Return value
        Return String.Empty
    End Function

    Private Function ExtractAlbumId(pathname As String) As String
        Dim match As Match = Regex.Match(pathname, "^/album/([^/]+)", RegexOptions.IgnoreCase)
        If match.Success Then Return match.Groups(1).Value
        Return String.Empty
    End Function

    Private Function ExtractTitle(html As String) As String
        Dim match As Match = Regex.Match(html, "<meta\s+property=""og:title""\s+content=""([^""]*)""", RegexOptions.IgnoreCase)
        If match.Success Then Return WebUtility.HtmlDecode(match.Groups(1).Value).Trim()
        Return String.Empty
    End Function

    Private Function LoadDownloadedImages(path As String) As List(Of Dictionary(Of String, String))
        Dim result As New List(Of Dictionary(Of String, String))()
        If Not File.Exists(path) Then Return result

        Try
            Using document As JsonDocument = JsonDocument.Parse(File.ReadAllText(path))
                Dim images As JsonElement
                If Not document.RootElement.TryGetProperty("images", images) OrElse images.ValueKind <> JsonValueKind.Array Then Return result

                For Each imageElement As JsonElement In images.EnumerateArray()
                    If imageElement.ValueKind <> JsonValueKind.Object Then Continue For
                    Dim image As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

                    For Each _property As JsonProperty In imageElement.EnumerateObject()
                        If _property.Value.ValueKind = JsonValueKind.String Then image(_property.Name) = _property.Value.GetString()
                    Next

                    If image.Count > 0 Then result.Add(image)
                Next
            End Using
        Catch ex As Exception
            Program.AddError("ImgBB: impossibile leggere il registro " & path & ": " & ex.Message)
        End Try

        Return result
    End Function

    Private Function GetAlbumDirectory(parentDirectory As String, albumTitle As String, albumId As String) As String
        Dim candidate As String = Path.Combine(parentDirectory, albumTitle)
        If Not Directory.Exists(candidate) Then Return candidate

        Dim metadataPath As String = Path.Combine(candidate, "album-readable.json")
        If File.Exists(metadataPath) Then
            Try
                Using document As JsonDocument = JsonDocument.Parse(File.ReadAllText(metadataPath))
                    Dim storedId As JsonElement
                    If document.RootElement.TryGetProperty("albumId", storedId) AndAlso storedId.ValueKind = JsonValueKind.String AndAlso String.Equals(storedId.GetString(), albumId, StringComparison.OrdinalIgnoreCase) Then Return candidate
                End Using
            Catch ex As JsonException
                Program.AddError("ImgBB: JSON di identificazione album non leggibile in " & metadataPath & ": " & ex.Message)
            End Try
        End If

        ' Non ho mai avuto occasione di eseguire la parte che segue
        Dim number As Integer = 2
        Do
            Dim numbered As String = Path.Combine(parentDirectory, albumTitle & "_" & number)
            If Not Directory.Exists(numbered) Then Return numbered
            number += 1
        Loop
    End Function

    Private Sub SaveAlbumJson(directory As String, albumId As String, albumTitle As String, rawPages As List(Of JsonElement), images As List(Of Dictionary(Of String, String)), subAlbums As List(Of Dictionary(Of String, String)))
        Dim options As New JsonSerializerOptions With {.WriteIndented = True}
        Try
            File.WriteAllText(Path.Combine(directory, "album.json"), JsonSerializer.Serialize(rawPages, options))
            File.WriteAllText(Path.Combine(directory, "album-readable.json"), JsonSerializer.Serialize(New With {.site = "ImgBB", .albumId = albumId, .title = albumTitle, .images = images, .subAlbums = subAlbums}, options))
        Catch ex As Exception
            Program.AddError("ImgBB: impossibile salvare i JSON dell'album " & albumId & ": " & ex.Message)
        End Try
    End Sub

    Private Sub SaveUserJson(directory As String, username As String, rawPages As List(Of JsonElement), albums As List(Of Dictionary(Of String, String)))
        Dim options As New JsonSerializerOptions With {.WriteIndented = True}
        Try
            File.WriteAllText(Path.Combine(directory, "albums.json"), JsonSerializer.Serialize(rawPages, options))
            File.WriteAllText(Path.Combine(directory, "albums-readable.json"), JsonSerializer.Serialize(New With {.site = "ImgBB", .username = username, .albums = albums}, options))
        Catch ex As Exception
            Program.AddError("ImgBB: impossibile salvare il JSON degli album dell'utente " & username & ": " & ex.Message)
        End Try
    End Sub

End Module
