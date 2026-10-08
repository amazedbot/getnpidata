Option Strict On

Imports System.Configuration
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Text
'Imports System.Text.RegularExpressions
Imports System.Windows.Forms
Imports ExcelDataReader
Imports MySql.Data.MySqlClient
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic.FileIO


Module Module1
    Public sMySqlConnectionString As String

    Sub Main()
        Dim BegTime As Date = Now
        ' SQL_Server_connection
        ' MySQL_connection
        Dim sMySqlConnectionString = ConfigurationManager.ConnectionStrings("MySQL_connection").ConnectionString

        Dim sTableName As String = "npidata"
        ' https://download.cms.gov/nppes/NPI_Files.html More info on the files. Some are full replacement, some are incremental. Some are deactivations. Some are other names.
        Dim myURIbase As String = "https://download.cms.gov/nppes/"
        Dim myFilename As String = "NPI_Files.html"
        Dim myUri As New Uri(myURIbase + myFilename)
        Dim WB As New BrowseWeb
        Dim ZipFilelist = WB.GetFileList(myUri)
        Dim Months = GetMonthNamesInList()
        Dim foutTemplateName As String = "C:\temp\short.csv"

        Using db As New DB(sMySqlConnectionString)
            Console.WriteLine()
            ' Need the following to insert data from file? Otherwise, you have to run this from MySQL Workbench on the database.
            db.PerformSQLcommand("SET GLOBAL local_infile = 1;")

            'db.TruncateTable(sTableName)
            Dim LoadCtr As Integer = 1
            For Each url As String In ZipFilelist
                Debug.WriteLine(url)
                Dim uri As New Uri(url)
                Dim filename = Path.GetFileName(uri.LocalPath)
                ' Check for _V2 for Monthly file. Only use that one (?)
                If Not NPIfileSeen(filename, sMySqlConnectionString) Then ' If Zip file wasn't seen already.
                    Dim FileLocation = GetNPIdata(myURIbase, filename)
                    ' Dim startPath = Path.GetDirectoryName(FileLocation)
                    Dim UnzipPath As String = ConfigurationManager.AppSettings("UnzipFolderName")
                    'Dim UnzipPath = startPath + "\Unzips"
                    Dim ZipPath = Path.GetDirectoryName(FileLocation) + "\" + filename

                    Console.WriteLine()
                    Console.WriteLine("Unzipping starting on: " + ZipPath + " ...")
                    Dim FileList = UnZip(ZipPath, UnzipPath, sMySqlConnectionString)
                    Console.WriteLine("Done Unzipping.")

                    ' Dim values = {"_Weekly", "_fileHeader", "endpoint_", "pl_pfile_", "othername_"}
                    ' Todo: endpoint_pfile are endpoints. Make another table for it.

                    For Each Fullname In FileList
                        Debug.WriteLine("Fullname= " + Fullname)

                        ' TODO: Add support for these skipped files.
                        If Fullname.Contains("_Weekly") OrElse
                           Fullname.Contains("_fileheader") OrElse
                           Fullname.Contains("endpoint_") Then
                            'Fullname.Contains("pl_pfile_") Then


                            ' If values.Any(Function(s) Fullname.Contains(s)) Then
                            ' skip any in values list.
                            Console.WriteLine("Skipping because we're not handling this file: " + Fullname)
                            Continue For
                        End If

                        Console.WriteLine("Processing file " + Fullname + ". Filename= " + filename + " ...")
                        ' If a full load. Have you seen it before? If so, truncate table
                        ' and do a full load and skip updates.
                        ' TODO: Remember csv files also? Right now, we are assuming that they were not seen.

                        ' https://stackoverflow.com/questions/61813776/if-strings-contains-multiple-values
                        'If filename.Contains("NPPES_Data_Dissemination_") And FindAnyInString(filename, Months) And Fullname.Contains("npidata_pfile_") Then
                        '    'If filename.Contains("NPPES_Data_Dissemination_") And FindAnyInString(filename, Months) And Fullname.Contains("npidata_pfile_") And (Not Fullname.Contains("_Weekly") And Not Fullname.Contains("_fileHeader") And Not Fullname.Contains("endpoint_") And Not Fullname.Contains("pl_pfile_")) Then ' And Fullname.Contains(".csv") 
                        '    ' Contains Full Replacement File.
                        '    Console.WriteLine("Loading Full Replacement File. Truncating destination. LoadCtr= " + LoadCtr.ToString)
                        '    LoadFile(Fullname, sTableName, "npidata_temp", False, True, db, foutTemplateName)

                        'ElseIf filename.Contains("NPPES_Data_Dissemination_") And filename.Contains("_Weekly.zip") And Not Fullname.Contains("_fileHeader") And Fullname.Contains(".csv") And Fullname.Contains("npidata_pfile_") Then
                        '    ' Contains Weekly Incremental File. Same structure as full, but don't truncate main table. 
                        '    Console.WriteLine("Loading Incremental File. NOT Truncating destination. LoadCtr= " + LoadCtr.ToString)
                        '    LoadFile(Fullname, sTableName, "npidata_temp", True, False, db, foutTemplateName)

                        'ElseIf filename.Contains("NPPES_Deactivated_NPI_Report_") And filename.Contains(".zip") Then ' And Fullname.Contains(".xlsx")
                        '    ' Deactivations 
                        '    Console.WriteLine("Deactivations. LoadCtr= " + LoadCtr.ToString)
                        '    PrepareNPIDeactivations(filename, sMySqlConnectionString)
                        '    UpdateNPIDataWithDeactivations(sTableName, sMySqlConnectionString) ' Updates them with deactivation date.

                        'Else
                        If filename.Contains("NPPES_Data_Dissemination_") And Fullname.Contains("othername_pfile_") And Not Fullname.Contains("_fileHeader") And Fullname.Contains(".csv") Then
                            ' Other Names
                            Console.WriteLine("Other Names. LoadCtr= " + LoadCtr.ToString)
                            LoadOtherNames(Fullname, db)

                            'Else
                            'If filename.Contains("NPPES_Data_Dissemination_") And Fullname.Contains("pl_pfile_") And Not Fullname.Contains("_fileHeader") And Fullname.Contains(".csv") Then
                            '    ' Practice Locations. 
                            '    Console.WriteLine("Practice Locations. LoadCtr= " + LoadCtr.ToString)
                            '    ' truncate only on LoadCtr = 1?
                            '    ' Dim TruncateDestination As Boolean = (LoadCtr = 1)
                            '    LoadPracticeLocations(Fullname, db)
                        Else
                                Console.WriteLine("Skipping file: " + Fullname + " In zip file:" + filename + " LoadCtr= " + LoadCtr.ToString)
                        End If
                        LoadCtr += 1
                    Next
                Else
                    Console.WriteLine(filename + " seen. Skipping.")
                End If
            Next
        End Using


        Dim ElapsedTime As Long = DateDiff(DateInterval.Minute, BegTime, Now)
        Console.WriteLine("Time for program (minutes): " & Convert.ToString(ElapsedTime), "D")
        Console.WriteLine()
        Console.WriteLine("getnpidata program completed.")
#If DEBUG Then
        Console.WriteLine("Press Any key.")
        Console.ReadLine()
#End If
    End Sub

    Sub LoadOtherNames(FullFileName As String, db As DB)
        db.TruncateTable("other_names_temp")
        Console.WriteLine("Bulk Loading: Reading " + FullFileName + " into table other_names_temp")
        Dim count As Int32 = BulkLoad(db.SconnStr, FullFileName, "other_names_temp", New Integer() {3}, "MM/dd/yyyy")
        Console.WriteLine(count.ToString + " rows loaded into other_names_temp")

        ' https://oneuptime.com/blog/post/2026-03-31-mysql-fix-error-1292-incorrect-datetime-value/view
        db.PerformSQLcommand("SET SESSION sql_mode = TRIM(BOTH ',' FROM REPLACE(CONCAT(',', @@sql_mode, ','), ',NO_ZERO_DATE,', ','));")
        Console.WriteLine("Updated sql_mode to remove NO_ZERO_DATE. Running UPDATE on other_names_temp for Created_Date")
        db.PerformSQLcommand("UPDATE other_names_temp
                                SET Created_Date = NULL
                                WHERE Created_Date = '0000-00-00';")

        Console.WriteLine("Inserting rows into other_names from other_names_temp")
        db.PerformSQLcommand("INSERT INTO other_names  (NPI ,
                                                        Provider_Other_Organization_Name,
						                               `Provider_Other_Organization_Name_Type_Code`,
						                               `Created_Date`)                           
				            SELECT DISTINCT    `NPI` ,
								               `Provider_Other_Organization_Name`,
								               `Provider_Other_Organization_Name_Type_Code`,
								               `Created_Date`
				            FROM other_names_temp t
                            WHERE NOT EXISTS
                            (SELECT *
                            FROM other_names n
						    WHERE n.NPI = t.NPI
							 AND n.Provider_Other_Organization_Name = t.Provider_Other_Organization_Name
                             AND n.Provider_Other_Organization_Name_Type_Code = t.Provider_Other_Organization_Name_Type_Code
                             AND n.Created_Date = t.Created_Date);") ' Copy to main table
    End Sub

    Sub LoadPracticeLocations(FullFileName As String, db As DB)
        db.TruncateTable("practice_locations_temp") ' table without ID, AUTO_INCREMENT, and PRIMARY KEY. This is a temp table for bulk load.
        Console.WriteLine("Bulk Loading: Reading " + FullFileName + " into table practice_locations_temp")
        Dim count As Int32 = BulkLoad(db.SconnStr, FullFileName, "practice_locations_temp")
        Console.WriteLine(count.ToString + " rows loaded into practice_locations_temp")
        Console.WriteLine("Inserting rows into practice_locations from practice_locations_temp")
        db.PerformSQLcommand("INSERT INTO practice_locations (`NPI`,
                                               `Provider_Secondary_Practice_Location_Address_Line_1` ,
                                               `Provider_Secondary_Practice_Location_Address_Line_2` ,
                                               `Provider_Secondary_Practice_Location_Address_City_Name` ,
                                               `Provider_Secondary_Practice_Location_Address_State_Name` ,
                                               `Provider_Secondary_Practice_Location_Address_Postal_Code` ,
                                               `Provider_Secondary_Practice_Location_Address_Country_Code` ,
                                               `Provider_Secondary_Practice_Location_Address_Telephone_Number` ,
                                               `Provider_Secondary_Practice_Location_Address_Telephone_Extension`  ,
                                               `Provider_Practice_Location_Address_Fax_Number`)
                                        SELECT DISTINCT `NPI`,
                                               `Provider_Secondary_Practice_Location_Address_Line_1` ,
                                               `Provider_Secondary_Practice_Location_Address_Line_2` ,
                                               `Provider_Secondary_Practice_Location_Address_City_Name` ,
                                               `Provider_Secondary_Practice_Location_Address_State_Name` ,
                                               `Provider_Secondary_Practice_Location_Address_Postal_Code` ,
                                               `Provider_Secondary_Practice_Location_Address_Country_Code` ,
                                               `Provider_Secondary_Practice_Location_Address_Telephone_Number` ,
                                               `Provider_Secondary_Practice_Location_Address_Telephone_Extension`  ,
                                               `Provider_Practice_Location_Address_Fax_Number`  
                                FROM practice_locations_temp t
                                WHERE NOT EXISTS 
									(SELECT *
                                    FROM practice_locations pl
                                    WHERE  pl.NPI = t.NPI and 
										   pl.Provider_Secondary_Practice_Location_Address_Line_1 = t.Provider_Secondary_Practice_Location_Address_Line_1 and 
                                           pl.Provider_Secondary_Practice_Location_Address_Line_2 = t.Provider_Secondary_Practice_Location_Address_Line_2 and
                                           pl.Provider_Secondary_Practice_Location_Address_City_Name = t.Provider_Secondary_Practice_Location_Address_City_Name and 
                                           pl.Provider_Secondary_Practice_Location_Address_State_Name = t.Provider_Secondary_Practice_Location_Address_State_Name and
                                           pl.Provider_Secondary_Practice_Location_Address_Postal_Code = t.Provider_Secondary_Practice_Location_Address_Postal_Code and
                                           pl.Provider_Secondary_Practice_Location_Address_Country_Code = t.Provider_Secondary_Practice_Location_Address_Country_Code and
                                           pl.Provider_Secondary_Practice_Location_Address_Telephone_Number = t.Provider_Secondary_Practice_Location_Address_Telephone_Number and 
                                           pl.Provider_Secondary_Practice_Location_Address_Telephone_Extension = t.Provider_Secondary_Practice_Location_Address_Telephone_Extension and
                                           pl.Provider_Practice_Location_Address_Fax_Number = t.Provider_Practice_Location_Address_Fax_Number) 
                                     ;") ' Copy to main table
    End Sub

    Sub LoadFile(FullFileName As String, PermTableName As String, TempTableName As String, AvoidDupes As Boolean, TruncateDestination As Boolean, db As DB, foutTemplateName As String)

        If Not db.DoesTableExist(TempTableName) Then
            db.PerformSQLcommand("CREATE TABLE " + TempTableName + " LIKE " + PermTableName)
        Else
            db.TruncateTable(TempTableName)
        End If

        Console.WriteLine("Bulk Loading: Reading " + FullFileName + " into table " + TempTableName)
        BulkLoad(db.SconnStr, FullFileName, TempTableName, foutTemplateName)


        If TruncateDestination Then
            db.TruncateTable(PermTableName) ' On full load
        Else
            ' Update rows with matching NPI numbers.
            Console.WriteLine("Updating table on matching NPI Numbers")
            If db.UpdateTableToTable(TempTableName, PermTableName) Then ' Update
                Console.WriteLine("Done Updating " + PermTableName + " with " + TempTableName)
            Else
                Console.WriteLine("Error in attempting to update " + PermTableName + " with " + TempTableName)
            End If
        End If

        ' Copy from temp table to perm table.
        Console.WriteLine("Copying " + TempTableName + " to " + PermTableName + " for new rows")
        Dim StartTime As Date = Date.Now
        If db.CopyTableToTable(TempTableName, PermTableName, AvoidDupes) Then ' Insert
            Console.WriteLine("Done Copying " + TempTableName + " to " + PermTableName)
        Else
            Console.WriteLine("Error in attempting copy from " + TempTableName + " to " + PermTableName)
        End If



        ' Dim EndTime As Date = Date.Now
        Dim TotalTime As TimeSpan = Now - StartTime

        Console.WriteLine("Duration including copying and updating: " + TotalTime.ToString("hh':'mm':'ss"))


#If Not DEBUG Then
            db.TruncateTable(TempTableName)
#End If

    End Sub
    Sub PrepareNPIDeactivations(filePath As String, sConnStr As String)
        ' https://github.com/ExcelDataReader/ExcelDataReader

        ' Truncate table NPPES_Deactivated_NPI_Report
        Using db As New DB(sConnStr)
            db.TruncateTable("NPPES_Deactivated_NPI_Report")

            Using MySqlConnectionObject As New MySqlConnection(sConnStr)
                Using sqlCommand As New MySqlCommand()
                    With sqlCommand
                        .CommandText = "INSERT INTO NPPES_Deactivated_NPI_Report (NPI, NPPES_Deactivation_Date) values (@NPI, @NPPES_Deactivation_Date)"
                        .Connection = MySqlConnectionObject
                        .CommandType = CommandType.Text
                        .Parameters.Add("@NPI", MySqlDbType.Text)
                        .Parameters.Add("@NPPES_Deactivation_Date", MySqlDbType.Date)
                    End With
                    MySqlConnectionObject.Open()
                    Using stream = File.Open(filePath, FileMode.Open, FileAccess.Read)
                        Using reader As IExcelDataReader = ExcelReaderFactory.CreateOpenXmlReader(stream)
                            reader.Read() ' Skip 2 lines. Make better.
                            reader.Read()

                            Dim NPI As String,
                                NPPES_Deactivation_Date As String

                            'Do. Only used if the Excel file has multiple sheets.
                            While reader.Read()
                                NPI = reader.GetString(0)
                                NPPES_Deactivation_Date = reader.GetString(1)

                                sqlCommand.Parameters("@NPI").Value = NPI
                                sqlCommand.Parameters("@NPPES_Deactivation_Date").Value = Convert.ToDateTime(NPPES_Deactivation_Date)
                                sqlCommand.ExecuteNonQuery()
                                Console.WriteLine("{0}, {1}", NPI, NPPES_Deactivation_Date)
                            End While
                            'Loop While reader.NextResult()
                            reader.Close()
                        End Using
                    End Using
                End Using
                MySqlConnectionObject.Close()
            End Using
            Dim fileName As String = Path.GetFileName(filePath)
            db.Insert_FileName(fileName)
        End Using
    End Sub

    Sub UpdateNPIDataWithDeactivations(TableName As String, sConnStr As String)
        Dim SQLStr = "UPDATE " + TableName + " nd 
                        JOIN nppes_deactivated_npi_report dar
                        ON nd.NPI = dar.NPI
                        SET nd.NPI_Deactivation_Date = dar.NPPES_Deactivation_Date"

        Using db As New DB(sConnStr)
            db.PerformSQLcommand(SQLStr)
        End Using
    End Sub

    Sub DeleteNPIDataWithDeactivations(TableName As String, sConnStr As String)
        Dim SQLStr = "DELETE " + TableName +
                     " FROM " + TableName +
                     " JOIN NPPES_Deactivated_NPI_Report 
                       ON " + TableName + ".NPI = NPPES_Deactivated_NPI_Report.NPI"

        Using db As New DB(sConnStr)
            db.PerformSQLcommand(SQLStr)
        End Using
    End Sub

    Function FindAnyInString(searchStr As String, listOfStr As List(Of String)) As Boolean
        ' Does any part of searchstr appear in listOfStr
        Dim inAnyinList As Boolean = False
        For Each lst As String In listOfStr
            Debug.WriteLine(lst)
            If searchStr.Contains(lst) Then
                inAnyinList = True
                Exit For
            End If
        Next

        Return inAnyinList
    End Function

    Function GetMonthNamesInList() As List(Of String)
        ' Return Monthnames as List 
        Dim Months As New List(Of String)
        For m As Integer = 1 To 12
            Months.Add(MonthName(CInt(New DateTime(1, m, 1).Month.ToString)))
        Next
        Return Months
    End Function

    Function GetNPIdata(remoteUri As String, fileName As String) As String
        ' https://docs.microsoft.com/en-us/dotnet/api/system.net.webclient.downloadfile?view=net-5.0
        ' Download Zip File.

        '  Create a New WebClient instance.
        Dim myStringWebResource As String = remoteUri + fileName
        Using myWebClient As New WebClient()
            ' Concatenate the domain with the Web resource filename.
            Console.WriteLine("Downloading File {0} from {1} .......", fileName, myStringWebResource)
            ' Download the Web resource And save it into the current filesystem folder.
            Try
                myWebClient.DownloadFile(myStringWebResource, fileName)
            Catch e As WebException
                Console.WriteLine(e.Message)

                If e.Status = WebExceptionStatus.ProtocolError Then
                    Console.WriteLine("Status Code : {0}", CType(e.Response, HttpWebResponse).StatusCode)
                    Console.WriteLine("Status Description : {0}", CType(e.Response, HttpWebResponse).StatusDescription)
                End If

            Catch e As Exception
                ' https://docs.microsoft.com/en-us/dotnet/api/system.net.webexception.response?view=net-5.0
                Console.WriteLine(e.Message)

            End Try
        End Using
        Console.WriteLine("Successfully Downloaded File {0} from {1} ", fileName, myStringWebResource)
        Console.WriteLine("Downloaded file saved in the following file system folder: " + Reflection.Assembly.GetExecutingAssembly().Location)
        Return Reflection.Assembly.GetExecutingAssembly().Location
    End Function

    Function UnZip(zipPath As String, extractPath As String, sConnStr As String) As List(Of String)
        ' https://stackoverflow.com/questions/15464740/system-io-compression-and-zipfile-extract-and-overwrite
        Dim entryFullname As String
        Dim entryPath As String
        Dim entryFn As String
        Dim FileList As New List(Of String)
        Dim ZipFileName As String = Path.GetFileNameWithoutExtension(zipPath)
        Using archive As ZipArchive = ZipFile.OpenRead(zipPath)
            For Each entry As ZipArchiveEntry In archive.Entries
                entryFullname = Path.Combine(extractPath, entry.FullName)
                entryPath = Path.GetDirectoryName(entryFullname)
                If Not Directory.Exists(entryPath) Then
                    Directory.CreateDirectory(entryPath)
                End If

                entryFn = Path.GetFileName(entryFullname)
                If ((Not String.IsNullOrEmpty(entryFn)) And entryFn.Contains(".csv") And Not entryFn.Contains("_FileHeader")) Or (entryFn.Contains("NPPES_Deactivated_NPI_Report_") And entryFn.Contains(".xlsx")) Then ' And entryFn.Contains("npidata_pfile") 
                    Console.WriteLine("Extracting: " + entryFullname)
                    FileList.Add(entryFullname)
                    entry.ExtractToFile(entryFullname, True)
                    File.SetAttributes(entryFullname, FileAttributes.Normal)

                    Using db As New DB(sConnStr)
                        db.Insert_Extract(ZipFileName, entry.FullName)
                    End Using
                End If
            Next
        End Using
        Return FileList
    End Function

    Public Class BrowseWeb
        ' https://docs.microsoft.com/en-us/dotnet/api/system.windows.forms.htmlelementcollection?view=net-5.0
        Public Function GetFileList(url As Uri) As List(Of String)
            Using br = New WebBrowser()
                AddHandler br.DocumentCompleted, AddressOf Browser_DocumentCompleted
                br.Navigate(url)
                Application.Run()
                Return ListOfFiles(br.Document)
            End Using
        End Function

        Private Sub Browser_DocumentCompleted(sender As Object, e As WebBrowserDocumentCompletedEventArgs)
            Dim br = TryCast(sender, WebBrowser)
            If br.Url = e.Url Then
                Application.ExitThread()
            End If
        End Sub
    End Class


    Function ListOfFiles(doc As HtmlDocument) As List(Of String)
        ' Idea from: https://stackoverflow.com/questions/34074106/webbrowser-htmlelement-getattributehref-prepending-hostname
        Dim anchors As HtmlElementCollection = doc.GetElementsByTagName("a")
        Dim fileList As New List(Of String)
        Dim href As String
        For Each el As HtmlElement In anchors
            href = el.GetAttribute("href")
            If href.Contains(".zip") Then
                fileList.Add(href)
            End If
        Next
        Return fileList
    End Function



    Sub CreateTempTableFromTable(fromTableName As String, toTableName As String, sConnectionString As String)
        Dim strSQL As String = "CREATE TABLE " + toTableName + " FROM " + fromTableName + " LIMIT 0"
        Dim db As New DB(sConnectionString, strSQL)
        Dim isSeen As Boolean = db.GetScalarReturnBoolean()
    End Sub


    Sub BulkLoad(sConnectionString As String, fileName As String, tableName As String, foutTemplateName As String)
        ' https://stackoverflow.com/questions/42627806/mysqlbulkloader-from-datatable-vb-net
        ' https://mysqlconnector.net/api/mysqlconnector/mysqlbulkloadertype/
        Dim cols As List(Of String) = RetCsvCols()
        Dim csvPartFileList As List(Of String) = SplitCSVFile(fileName, foutTemplateName, 100000)

        Using MySqlConnectionObject As New MySqlConnection(sConnectionString + ";AllowLoadLocalInfile=True")
            Dim bulk = New MySqlBulkLoader(MySqlConnectionObject) With {
                .TableName = tableName,
                .FieldTerminator = ",",
                .LineTerminator = "\r\n",    ' == CR/LF vbCrLf
                .FieldQuotationCharacter = Chr(34),
                .Local = True}

            bulk.Columns.Clear()
            For Each s In cols
                bulk.Columns.Add(s)         ' specify col order in file
            Next
            Console.WriteLine()
            Dim count As Integer = 1
            Dim TotRows As Integer = 0
            For Each bf In csvPartFileList
                Console.Write("Loading split file: " + bf + "...")
                bulk.FileName = bf
                If count = 1 Then
                    bulk.NumberOfLinesToSkip = 1
                Else
                    bulk.NumberOfLinesToSkip = 0
                End If

                Dim rows = bulk.Load()
                Console.Write(rows.ToString + " rows loaded...")
                TotRows += rows
                DeleteFiles(bf)
                count += 1
            Next
            Console.WriteLine()
            Console.WriteLine("Total Split Rows loaded for " + fileName + " was " + TotRows.ToString())
            Console.WriteLine()
        End Using
        DeleteFiles(csvPartFileList)
    End Sub

    'BulkLoad(db.SconnStr, FullFileName, "other_names", cols)
    'Sub BulkLoad(sConnectionString As String, fileName As String, tableName As String, cols As List(Of String))
    ' https://stackoverflow.com/questions/42627806/mysqlbulkloader-from-datatable-vb-net
    ' https://mysqlconnector.net/api/mysqlconnector/mysqlbulkloadertype/
    'Dim cols As New List(Of String)(New String() {"NPI",
    '                                            "Provider_Other_Organization_Name",
    '                                            "Provider_Other_Organization_Name_Type_Code",
    '                                            "Created_Date"})

    'Dim fileName_formatted = Reformat_Dates(sConnectionString, fileName)

    'Using MySqlConnectionObject As New MySqlConnection(sConnectionString + ";AllowLoadLocalInfile=True")
    '    Dim bulk = New MySqlBulkLoader(MySqlConnectionObject) With {
    '        .TableName = tableName,
    '        .FieldTerminator = ",",
    '        .LineTerminator = "\n",    ' == LF vbLf
    '        .FieldQuotationCharacter = Chr(34),
    '        .Local = True}

    '    bulk.Columns.Clear()
    '    For Each s In cols
    '        bulk.Columns.Add(s)         ' specify col order in file
    '    Next
    '    Console.WriteLine()
    '    ' bulk.FileName = fileName
    '    bulk.FileName = fileName_formatted
    '    ' delete files...
    '    bulk.NumberOfLinesToSkip = 1 ' header row? Yes
    '    Dim rows = bulk.Load()
    '    Console.Write(rows.ToString + " rows loaded...")
    '    Console.WriteLine()

    'End Using
    'End Sub

    Function BulkLoad(connectionString As String, csvPath As String, tableName As String, Optional ByVal dateColumnIndexes As Integer() = Nothing, Optional ByVal sourceDateFormat As String = Nothing) As Int32


        Using parser As New TextFieldParser(csvPath)
            Dim sb As New StringBuilder()
            Dim count As Integer = 0
            parser.TextFieldType = FieldType.Delimited
            parser.SetDelimiters(",")
            parser.HasFieldsEnclosedInQuotes = True

            Dim headers() As String = parser.ReadFields() ' Skip header row

            While Not parser.EndOfData


                Dim cols() As String = parser.ReadFields()



                If dateColumnIndexes IsNot Nothing Then
                    For Each idx As Integer In dateColumnIndexes
                        If idx >= 0 AndAlso idx < cols.Count Then
                            ' Remove any leading/trailing quotes and empty entries
                            'cols(idx) = cols(idx).Replace("""", "").Trim()

                            Dim parsedDate As Date
                            If Date.TryParseExact(cols(idx), sourceDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, parsedDate) Then
                                ' Convert to MySQL DATE format
                                cols(idx) = parsedDate.ToString("yyyy-MM-dd")
                            ElseIf DateTime.TryParse(cols(idx), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, parsedDate) Then
                                cols(idx) = parsedDate.ToString("yyyy-MM-dd")
                            Else
                                ' MySQL NULL representation in bulk load
                                cols(idx) = "\N"
                            End If
                        End If
                    Next
                End If
                'For Each field As String In cols
                '    Console.WriteLine(field)
                'Next
                sb.AppendLine(String.Join(",", cols))

            End While
            ' Convert processed CSV to bytes
            Dim csvBytes() As Byte = Encoding.UTF8.GetBytes(sb.ToString())

            Using conn As New MySqlConnection(connectionString)
                conn.Open()
                ' MySqlBulkLoader only accepts file paths, so we create a temp file
                Dim tempFile As String = Path.GetTempFileName()
                File.WriteAllBytes(tempFile, csvBytes)
                Try
                    Dim bulk As New MySqlBulkLoader(conn) With {
                        .TableName = tableName,
                        .FieldTerminator = ",",
                        .LineTerminator = "\n",    ' == CR/LF vbCrLf
                        .FieldQuotationCharacter = Chr(34),
                        .NumberOfLinesToSkip = 0,
                        .Local = True,
                        .FileName = tempFile
                    }
                    count = bulk.Load()
                    Console.WriteLine($"Inserted {count} rows into {tableName}.")

                Finally
                    ' Clean up temp file
                    If File.Exists(tempFile) Then
                        File.Delete(tempFile)
                    End If
                End Try
                Return count
            End Using
        End Using

        'Return count
    End Function

    Function BulkLoad(connectionString As String, csvPath As String, tableName As String) As Int32 ', Optional ByVal dateColumnIndexes As Integer() = Nothing, Optional ByVal sourceDateFormat As String = Nothing) As Int32

        ' Read original CSV
        Dim lines() As String = File.ReadAllLines(csvPath)
        Dim sb As New StringBuilder()
        Dim count As Integer = 0
        Dim cols As List(Of String)

        For Each line As String In lines
            If String.IsNullOrWhiteSpace(line) Then Continue For

            cols = SplitCsvLine(line)
            'If dateColumnIndexes IsNot Nothing Then
            '    For Each idx As Integer In dateColumnIndexes
            '        If idx >= 0 AndAlso idx < cols.Count Then
            '            ' Remove any leading/trailing quotes and empty entries
            '            cols(idx) = cols(idx).Replace("""", "").Trim()

            '            Dim parsedDate As Date
            '            If Date.TryParseExact(cols(idx), sourceDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, parsedDate) Then
            '                ' Convert to MySQL DATE format
            '                cols(idx) = parsedDate.ToString("yyyy-MM-dd")
            '            ElseIf DateTime.TryParse(cols(idx), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, parsedDate) Then
            '                cols(idx) = parsedDate.ToString("yyyy-MM-dd")
            '            Else
            '                ' MySQL NULL representation in bulk load
            '                cols(idx) = "\N"
            '            End If
            '        End If
            '    Next
            'End If
            sb.AppendLine(String.Join(",", cols))
        Next

        ' Convert processed CSV to bytes
        Dim csvBytes() As Byte = Encoding.UTF8.GetBytes(sb.ToString())

        Using conn As New MySqlConnection(connectionString)
            conn.Open()

            ' MySqlBulkLoader only accepts file paths, so we create a temp file
            Dim tempFile As String = Path.GetTempFileName()
            File.WriteAllBytes(tempFile, csvBytes)

            Try
                Dim bulk As New MySqlBulkLoader(conn) With {
                    .TableName = tableName,
                    .FieldTerminator = ",",
                    .LineTerminator = "\n",    ' == CR/LF vbCrLf
                    .FieldQuotationCharacter = Chr(34),
                    .NumberOfLinesToSkip = 1, ' changed from 0
                    .Local = True,
                    .FileName = tempFile
                }

                count = bulk.Load()
                Console.WriteLine($"Inserted {count} rows into {tableName}.")

            Finally
                ' Clean up temp file
                If File.Exists(tempFile) Then
                    File.Delete(tempFile)
                End If
            End Try
            Return count
        End Using

    End Function


    ''' <summary>
    ''' Splits a CSV line into columns, respecting quoted fields.
    ''' </summary>
    ''' <param name="csvLine">A single line from a CSV file.</param>
    ''' <returns>List of column values as strings.</returns>
    Function SplitCsvLine(csvLine As String) As List(Of String)
        Dim columns As New List(Of String)()

        If String.IsNullOrEmpty(csvLine) Then
            Return columns
        End If

        ' Regex pattern: match quoted fields or unquoted fields
        Dim pattern As String = "(?<=^|,)(?:""(?<val>(?:[^""]|"""")*)""|(?<val>[^,]*))"
        Dim matches As MatchCollection = Regex.Matches(csvLine, pattern)

        For Each m As Match In matches
            ' Replace double double-quotes with a single double-quote
            Dim value As String = m.Groups("val").Value.Replace("""""", """")
            columns.Add(value)
        Next

        Return columns
    End Function



    Function SplitCSVFile(fileNameIn As String, fileNameOut As String, linesToWrite As Integer) As List(Of String)
        Dim SplitFileList As New List(Of String)
        Using sr As New StreamReader(fileNameIn)
            Dim count As Integer = 0
            Dim fileNumber As Integer = 0

            While Not sr.EndOfStream
                Try
                    fileNumber += 1
                    Dim fileOutNameNumber As String = Path.GetFullPath(fileNameOut) + fileNumber.ToString + Path.GetExtension(fileNameOut)
                    Using sw As New StreamWriter(fileOutNameNumber) With {
                            .AutoFlush = True}
                        While Not sr.EndOfStream And count <= linesToWrite
                            sw.WriteLine(sr.ReadLine(), True)
                            'sw.Flush()
                            count += 1
                        End While
                        sw.Close()
                    End Using

                    SplitFileList.Add(fileOutNameNumber)
                    Console.WriteLine("Created split file: " + fileOutNameNumber + ". " + count.ToString + " rows")
                    count = 0
                Catch
                    Threading.Thread.Sleep(1000)
                End Try
            End While
            sr.Close()
        End Using

        Return SplitFileList
    End Function

    Sub DeleteFiles(listOfFiles As List(Of String))
        For Each f As String In listOfFiles
            If File.Exists(f) Then
                Console.WriteLine("Deleting file: " + f)
                File.Delete(f)
            End If
        Next
    End Sub

    Sub DeleteFiles(FileName As String)
        ' Wow: Method Overloading ;)
        If File.Exists(FileName) Then
            Console.WriteLine("Deleting file: " + FileName)
            File.Delete(FileName)
        End If
    End Sub

    Function RetCsvColsAsString() As String
        Return String.Join(",", RetCsvCols())
    End Function

    Function RetCsvCols() As List(Of String)
        Dim csvCols As New List(Of String)(New String() {
        "NPI",
        "Entity_Type_Code",
        "Replacement_NPI",
        "Employer_Identification_Number_EIN",
        "Provider_Organization_Name_Legal_Business_Name",
        "Provider_Last_Name_Legal_Name",
        "Provider_First_Name",
        "Provider_Middle_Name",
        "Provider_Name_Prefix_Text",
        "Provider_Name_Suffix_Text",
        "Provider_Credential_Text",
        "Provider_Other_Organization_Name",
        "Provider_Other_Organization_Name_Type_Code",
        "Provider_Other_Last_Name",
        "Provider_Other_First_Name",
        "Provider_Other_Middle_Name",
        "Provider_Other_Name_Prefix_Text",
        "Provider_Other_Name_Suffix_Text",
        "Provider_Other_Credential_Text",
        "Provider_Other_Last_Name_Type_Code",
        "Provider_First_Line_Business_Mailing_Address",
        "Provider_Second_Line_Business_Mailing_Address",
        "Provider_Business_Mailing_Address_City_Name",
        "Provider_Business_Mailing_Address_State_Name",
        "Provider_Business_Mailing_Address_Postal_Code",
        "Provider_Business_Mailing_Address_Country_Code",
        "Provider_Business_Mailing_Address_Telephone_Number",
        "Provider_Business_Mailing_Address_Fax_Number",
        "Provider_First_Line_Business_Practice_Location_Address",
        "Provider_Second_Line_Business_Practice_Location_Address",
        "Provider_Business_Practice_Location_Address_City_Name",
        "Provider_Business_Practice_Location_Address_State_Name",
        "Provider_Business_Practice_Location_Address_Postal_Code",
        "Provider_Business_Practice_Location_Address_Country_Code",
        "Provider_Business_Practice_Location_Address_Telephone_Number",
        "Provider_Business_Practice_Location_Address_Fax_Number",
        "Provider_Enumeration_Date",
        "Last_Update_Date",
        "NPI_Deactivation_Reason_Code",
        "NPI_Deactivation_Date",
        "NPI_Reactivation_Date",
        "Provider_Gender_Code",
        "Authorized_Official_Last_Name",
        "Authorized_Official_First_Name",
        "Authorized_Official_Middle_Name",
        "Authorized_Official_Title_or_Position",
        "Authorized_Official_Telephone_Number",
        "Healthcare_Provider_Taxonomy_Code_1",
        "Provider_License_Number_1",
        "Provider_License_Number_State_Code_1",
        "Healthcare_Provider_Primary_Taxonomy_Switch_1",
        "Healthcare_Provider_Taxonomy_Code_2",
        "Provider_License_Number_2",
        "Provider_License_Number_State_Code_2",
        "Healthcare_Provider_Primary_Taxonomy_Switch_2",
        "Healthcare_Provider_Taxonomy_Code_3",
        "Provider_License_Number_3",
        "Provider_License_Number_State_Code_3",
        "Healthcare_Provider_Primary_Taxonomy_Switch_3",
        "Healthcare_Provider_Taxonomy_Code_4",
        "Provider_License_Number_4",
        "Provider_License_Number_State_Code_4",
        "Healthcare_Provider_Primary_Taxonomy_Switch_4",
        "Healthcare_Provider_Taxonomy_Code_5",
        "Provider_License_Number_5",
        "Provider_License_Number_State_Code_5",
        "Healthcare_Provider_Primary_Taxonomy_Switch_5",
        "Healthcare_Provider_Taxonomy_Code_6",
        "Provider_License_Number_6",
        "Provider_License_Number_State_Code_6",
        "Healthcare_Provider_Primary_Taxonomy_Switch_6",
        "Healthcare_Provider_Taxonomy_Code_7",
        "Provider_License_Number_7",
        "Provider_License_Number_State_Code_7",
        "Healthcare_Provider_Primary_Taxonomy_Switch_7",
        "Healthcare_Provider_Taxonomy_Code_8",
        "Provider_License_Number_8",
        "Provider_License_Number_State_Code_8",
        "Healthcare_Provider_Primary_Taxonomy_Switch_8",
        "Healthcare_Provider_Taxonomy_Code_9",
        "Provider_License_Number_9",
        "Provider_License_Number_State_Code_9",
        "Healthcare_Provider_Primary_Taxonomy_Switch_9",
        "Healthcare_Provider_Taxonomy_Code_10",
        "Provider_License_Number_10",
        "Provider_License_Number_State_Code_10",
        "Healthcare_Provider_Primary_Taxonomy_Switch_10",
        "Healthcare_Provider_Taxonomy_Code_11",
        "Provider_License_Number_11",
        "Provider_License_Number_State_Code_11",
        "Healthcare_Provider_Primary_Taxonomy_Switch_11",
        "Healthcare_Provider_Taxonomy_Code_12",
        "Provider_License_Number_12",
        "Provider_License_Number_State_Code_12",
        "Healthcare_Provider_Primary_Taxonomy_Switch_12",
        "Healthcare_Provider_Taxonomy_Code_13",
        "Provider_License_Number_13",
        "Provider_License_Number_State_Code_13",
        "Healthcare_Provider_Primary_Taxonomy_Switch_13",
        "Healthcare_Provider_Taxonomy_Code_14",
        "Provider_License_Number_14",
        "Provider_License_Number_State_Code_14",
        "Healthcare_Provider_Primary_Taxonomy_Switch_14",
        "Healthcare_Provider_Taxonomy_Code_15",
        "Provider_License_Number_15",
        "Provider_License_Number_State_Code_15",
        "Healthcare_Provider_Primary_Taxonomy_Switch_15",
        "Other_Provider_Identifier_1",
        "Other_Provider_Identifier_Type_Code_1",
        "Other_Provider_Identifier_State_1",
        "Other_Provider_Identifier_Issuer_1",
        "Other_Provider_Identifier_2",
        "Other_Provider_Identifier_Type_Code_2",
        "Other_Provider_Identifier_State_2",
        "Other_Provider_Identifier_Issuer_2",
        "Other_Provider_Identifier_3",
        "Other_Provider_Identifier_Type_Code_3",
        "Other_Provider_Identifier_State_3",
        "Other_Provider_Identifier_Issuer_3",
        "Other_Provider_Identifier_4",
        "Other_Provider_Identifier_Type_Code_4",
        "Other_Provider_Identifier_State_4",
        "Other_Provider_Identifier_Issuer_4",
        "Other_Provider_Identifier_5",
        "Other_Provider_Identifier_Type_Code_5",
        "Other_Provider_Identifier_State_5",
        "Other_Provider_Identifier_Issuer_5",
        "Other_Provider_Identifier_6",
        "Other_Provider_Identifier_Type_Code_6",
        "Other_Provider_Identifier_State_6",
        "Other_Provider_Identifier_Issuer_6",
        "Other_Provider_Identifier_7",
        "Other_Provider_Identifier_Type_Code_7",
        "Other_Provider_Identifier_State_7",
        "Other_Provider_Identifier_Issuer_7",
        "Other_Provider_Identifier_8",
        "Other_Provider_Identifier_Type_Code_8",
        "Other_Provider_Identifier_State_8",
        "Other_Provider_Identifier_Issuer_8",
        "Other_Provider_Identifier_9",
        "Other_Provider_Identifier_Type_Code_9",
        "Other_Provider_Identifier_State_9",
        "Other_Provider_Identifier_Issuer_9",
        "Other_Provider_Identifier_10",
        "Other_Provider_Identifier_Type_Code_10",
        "Other_Provider_Identifier_State_10",
        "Other_Provider_Identifier_Issuer_10",
        "Other_Provider_Identifier_11",
        "Other_Provider_Identifier_Type_Code_11",
        "Other_Provider_Identifier_State_11",
        "Other_Provider_Identifier_Issuer_11",
        "Other_Provider_Identifier_12",
        "Other_Provider_Identifier_Type_Code_12",
        "Other_Provider_Identifier_State_12",
        "Other_Provider_Identifier_Issuer_12",
        "Other_Provider_Identifier_13",
        "Other_Provider_Identifier_Type_Code_13",
        "Other_Provider_Identifier_State_13",
        "Other_Provider_Identifier_Issuer_13",
        "Other_Provider_Identifier_14",
        "Other_Provider_Identifier_Type_Code_14",
        "Other_Provider_Identifier_State_14",
        "Other_Provider_Identifier_Issuer_14",
        "Other_Provider_Identifier_15",
        "Other_Provider_Identifier_Type_Code_15",
        "Other_Provider_Identifier_State_15",
        "Other_Provider_Identifier_Issuer_15",
        "Other_Provider_Identifier_16",
        "Other_Provider_Identifier_Type_Code_16",
        "Other_Provider_Identifier_State_16",
        "Other_Provider_Identifier_Issuer_16",
        "Other_Provider_Identifier_17",
        "Other_Provider_Identifier_Type_Code_17",
        "Other_Provider_Identifier_State_17",
        "Other_Provider_Identifier_Issuer_17",
        "Other_Provider_Identifier_18",
        "Other_Provider_Identifier_Type_Code_18",
        "Other_Provider_Identifier_State_18",
        "Other_Provider_Identifier_Issuer_18",
        "Other_Provider_Identifier_19",
        "Other_Provider_Identifier_Type_Code_19",
        "Other_Provider_Identifier_State_19",
        "Other_Provider_Identifier_Issuer_19",
        "Other_Provider_Identifier_20",
        "Other_Provider_Identifier_Type_Code_20",
        "Other_Provider_Identifier_State_20",
        "Other_Provider_Identifier_Issuer_20",
        "Other_Provider_Identifier_21",
        "Other_Provider_Identifier_Type_Code_21",
        "Other_Provider_Identifier_State_21",
        "Other_Provider_Identifier_Issuer_21",
        "Other_Provider_Identifier_22",
        "Other_Provider_Identifier_Type_Code_22",
        "Other_Provider_Identifier_State_22",
        "Other_Provider_Identifier_Issuer_22",
        "Other_Provider_Identifier_23",
        "Other_Provider_Identifier_Type_Code_23",
        "Other_Provider_Identifier_State_23",
        "Other_Provider_Identifier_Issuer_23",
        "Other_Provider_Identifier_24",
        "Other_Provider_Identifier_Type_Code_24",
        "Other_Provider_Identifier_State_24",
        "Other_Provider_Identifier_Issuer_24",
        "Other_Provider_Identifier_25",
        "Other_Provider_Identifier_Type_Code_25",
        "Other_Provider_Identifier_State_25",
        "Other_Provider_Identifier_Issuer_25",
        "Other_Provider_Identifier_26",
        "Other_Provider_Identifier_Type_Code_26",
        "Other_Provider_Identifier_State_26",
        "Other_Provider_Identifier_Issuer_26",
        "Other_Provider_Identifier_27",
        "Other_Provider_Identifier_Type_Code_27",
        "Other_Provider_Identifier_State_27",
        "Other_Provider_Identifier_Issuer_27",
        "Other_Provider_Identifier_28",
        "Other_Provider_Identifier_Type_Code_28",
        "Other_Provider_Identifier_State_28",
        "Other_Provider_Identifier_Issuer_28",
        "Other_Provider_Identifier_29",
        "Other_Provider_Identifier_Type_Code_29",
        "Other_Provider_Identifier_State_29",
        "Other_Provider_Identifier_Issuer_29",
        "Other_Provider_Identifier_30",
        "Other_Provider_Identifier_Type_Code_30",
        "Other_Provider_Identifier_State_30",
        "Other_Provider_Identifier_Issuer_30",
        "Other_Provider_Identifier_31",
        "Other_Provider_Identifier_Type_Code_31",
        "Other_Provider_Identifier_State_31",
        "Other_Provider_Identifier_Issuer_31",
        "Other_Provider_Identifier_32",
        "Other_Provider_Identifier_Type_Code_32",
        "Other_Provider_Identifier_State_32",
        "Other_Provider_Identifier_Issuer_32",
        "Other_Provider_Identifier_33",
        "Other_Provider_Identifier_Type_Code_33",
        "Other_Provider_Identifier_State_33",
        "Other_Provider_Identifier_Issuer_33",
        "Other_Provider_Identifier_34",
        "Other_Provider_Identifier_Type_Code_34",
        "Other_Provider_Identifier_State_34",
        "Other_Provider_Identifier_Issuer_34",
        "Other_Provider_Identifier_35",
        "Other_Provider_Identifier_Type_Code_35",
        "Other_Provider_Identifier_State_35",
        "Other_Provider_Identifier_Issuer_35",
        "Other_Provider_Identifier_36",
        "Other_Provider_Identifier_Type_Code_36",
        "Other_Provider_Identifier_State_36",
        "Other_Provider_Identifier_Issuer_36",
        "Other_Provider_Identifier_37",
        "Other_Provider_Identifier_Type_Code_37",
        "Other_Provider_Identifier_State_37",
        "Other_Provider_Identifier_Issuer_37",
        "Other_Provider_Identifier_38",
        "Other_Provider_Identifier_Type_Code_38",
        "Other_Provider_Identifier_State_38",
        "Other_Provider_Identifier_Issuer_38",
        "Other_Provider_Identifier_39",
        "Other_Provider_Identifier_Type_Code_39",
        "Other_Provider_Identifier_State_39",
        "Other_Provider_Identifier_Issuer_39",
        "Other_Provider_Identifier_40",
        "Other_Provider_Identifier_Type_Code_40",
        "Other_Provider_Identifier_State_40",
        "Other_Provider_Identifier_Issuer_40",
        "Other_Provider_Identifier_41",
        "Other_Provider_Identifier_Type_Code_41",
        "Other_Provider_Identifier_State_41",
        "Other_Provider_Identifier_Issuer_41",
        "Other_Provider_Identifier_42",
        "Other_Provider_Identifier_Type_Code_42",
        "Other_Provider_Identifier_State_42",
        "Other_Provider_Identifier_Issuer_42",
        "Other_Provider_Identifier_43",
        "Other_Provider_Identifier_Type_Code_43",
        "Other_Provider_Identifier_State_43",
        "Other_Provider_Identifier_Issuer_43",
        "Other_Provider_Identifier_44",
        "Other_Provider_Identifier_Type_Code_44",
        "Other_Provider_Identifier_State_44",
        "Other_Provider_Identifier_Issuer_44",
        "Other_Provider_Identifier_45",
        "Other_Provider_Identifier_Type_Code_45",
        "Other_Provider_Identifier_State_45",
        "Other_Provider_Identifier_Issuer_45",
        "Other_Provider_Identifier_46",
        "Other_Provider_Identifier_Type_Code_46",
        "Other_Provider_Identifier_State_46",
        "Other_Provider_Identifier_Issuer_46",
        "Other_Provider_Identifier_47",
        "Other_Provider_Identifier_Type_Code_47",
        "Other_Provider_Identifier_State_47",
        "Other_Provider_Identifier_Issuer_47",
        "Other_Provider_Identifier_48",
        "Other_Provider_Identifier_Type_Code_48",
        "Other_Provider_Identifier_State_48",
        "Other_Provider_Identifier_Issuer_48",
        "Other_Provider_Identifier_49",
        "Other_Provider_Identifier_Type_Code_49",
        "Other_Provider_Identifier_State_49",
        "Other_Provider_Identifier_Issuer_49",
        "Other_Provider_Identifier_50",
        "Other_Provider_Identifier_Type_Code_50",
        "Other_Provider_Identifier_State_50",
        "Other_Provider_Identifier_Issuer_50",
        "Is_Sole_Proprietor",
        "Is_Organization_Subpart",
        "Parent_Organization_LBN",
        "Parent_Organization_TIN",
        "Authorized_Official_Name_Prefix_Text",
        "Authorized_Official_Name_Suffix_Text",
        "Authorized_Official_Credential_Text",
        "Healthcare_Provider_Taxonomy_Group_1",
        "Healthcare_Provider_Taxonomy_Group_2",
        "Healthcare_Provider_Taxonomy_Group_3",
        "Healthcare_Provider_Taxonomy_Group_4",
        "Healthcare_Provider_Taxonomy_Group_5",
        "Healthcare_Provider_Taxonomy_Group_6",
        "Healthcare_Provider_Taxonomy_Group_7",
        "Healthcare_Provider_Taxonomy_Group_8",
        "Healthcare_Provider_Taxonomy_Group_9",
        "Healthcare_Provider_Taxonomy_Group_10",
        "Healthcare_Provider_Taxonomy_Group_11",
        "Healthcare_Provider_Taxonomy_Group_12",
        "Healthcare_Provider_Taxonomy_Group_13",
        "Healthcare_Provider_Taxonomy_Group_14",
        "Healthcare_Provider_Taxonomy_Group_15",
        "Certification_Date"})
        Return csvCols
    End Function


    Function NPIfileSeen(fileName As String, sConnectionString As String) As Boolean
        Dim strSQL As String = "SELECT COUNT(*) FROM DownLog where filename =  '" + fileName + "'"
        Using db As New DB(sConnectionString, strSQL)
            Dim isSeen As Boolean = db.GetScalarReturnBoolean()
            If Not isSeen Then
                db.Insert_FileName(fileName) ' Save filename in table.
            End If
            Return isSeen
        End Using
    End Function
    Public Class DB

        Implements IDisposable

        ' https://stackoverflow.com/questions/59056680/how-do-i-retrieve-a-value-from-an-sql-query-and-store-it-in-a-variable-in-vb-net
        ' https://stackoverflow.com/questions/3279106/how-to-implement-class-constructor-in-visual-basic

        Public ReadOnly SconnStr As String
        Private ReadOnly sSQLStr As String


        Public Sub New(newSconnStr As String, newSQLStr As String)
            SconnStr = newSconnStr
            sSQLStr = newSQLStr
        End Sub

        Public Sub New(newSconnStr As String)
            SconnStr = newSconnStr
        End Sub

        Function GetScalarReturnBoolean() As Boolean
            ' Returns true if value returned is greater than 0.
            Dim Retval As Boolean
            Using con As New MySqlConnection(SconnStr),
                sqlQuery As New MySqlCommand(sSQLStr, con)
                With sqlQuery
                    .CommandTimeout = 999999
                End With
                con.Open()
                Retval = Convert.ToBoolean(sqlQuery.ExecuteScalar()) ' returns true if non-zero.
                con.Close()
            End Using
            Return Retval
        End Function

        Function Insert_FileName(filename As String) As Boolean
            ' https://stackoverflow.com/questions/9234753/inserting-data-into-a-mysql-table-using-vb-net
            Dim iReturn As Boolean
            Using con As New MySqlConnection(SconnStr)
                Using sqlCommand As New MySqlCommand()
                    With sqlCommand
                        .CommandText = "INSERT INTO downlog (filename) values (@filename)"
                        .Connection = con
                        .CommandType = CommandType.Text
                        .Parameters.AddWithValue("@filename", filename)
                    End With
                    Try
                        con.Open()
                        sqlCommand.ExecuteNonQuery()
                        iReturn = True
                    Catch ex As MySqlException
                        Console.WriteLine("Error in sql: INSERT INTO downlog...")
                        Console.WriteLine(ex.Message)
                        iReturn = False
                    Finally
                        con.Close()
                    End Try
                End Using
            End Using

            Return iReturn
        End Function


        Function Insert_Extract(Zipfilename As String, ExtractFileName As String) As Boolean
            ' https://stackoverflow.com/questions/9234753/inserting-data-into-a-mysql-table-using-vb-net
            Dim iReturn As Boolean
            Using con As New MySqlConnection(SconnStr)
                Using sqlCommand As New MySqlCommand()
                    With sqlCommand
                        .CommandText = "INSERT INTO extractlog (ZipFileName, ExtractFileName) values (@Zipfilename, @ExtractFileName)"
                        .Connection = con
                        .CommandType = CommandType.Text
                        .Parameters.AddWithValue("@Zipfilename", Zipfilename)
                        .Parameters.AddWithValue("@ExtractFileName", ExtractFileName)
                    End With
                    Try
                        con.Open()
                        sqlCommand.ExecuteNonQuery()
                        iReturn = True
                    Catch ex As MySqlException
                        Console.WriteLine("Error in sql: INSERT INTO extractlog...")
                        Console.WriteLine(ex.Message)
                        iReturn = False
                    Finally
                        con.Close()
                    End Try
                End Using
            End Using

            Return iReturn
        End Function

        Function PerformSQLcommand(strcommand As String) As Boolean
            ' TODO: Allow parameters...
            Dim iReturn As Boolean = False
            Using con As New MySqlConnection(SconnStr)
                Using sqlCommand As New MySqlCommand()
                    With sqlCommand
                        .CommandText = strcommand
                        .Connection = con
                        .CommandType = CommandType.Text
                        .CommandTimeout = 999999
                    End With
                    Try
                        con.Open()
                        Dim rows = sqlCommand.ExecuteNonQuery()
                        Console.WriteLine(rows.ToString() + " rows affected.")
                        iReturn = True
                    Catch ex As MySqlException
                        Console.WriteLine("Error in sql: " + strcommand)
                        Console.WriteLine(ex.Message)
                        iReturn = False
                    Finally
                        con.Close()
                    End Try
                End Using
            End Using
            Return iReturn
        End Function



        Function CopyTableToTable(FromTable As String, ToTable As String, AvoidDupes As Boolean) As Boolean
            Dim ColNames As String = RetCsvColsAsString()
            Dim SqlCmd As String = "INSERT INTO " + ToTable + "(" + ColNames + ") " +
                " SELECT " + ColNames + " FROM " + FromTable
            If AvoidDupes Then
                SqlCmd += " WHERE NPI NOT IN (SELECT NPI FROM " + ToTable + " ORDER BY NPI) "
            End If
            SqlCmd += " ORDER BY NPI"

            Dim CopyWorked As Boolean = PerformSQLcommand(SqlCmd)
            Return CopyWorked
        End Function

        Function UpdateTableToTable(FromTable As String, ToTable As String) As Boolean
            Dim FieldList = RetCsvCols()

            Dim UpdateFields = ""
            For Each item In FieldList
                If item = "NPI" Then
                    Continue For
                End If
                UpdateFields += "t." + item + " = f." + item
                If item <> "Certification_Date" Then
                    UpdateFields += ", "
                End If
                UpdateFields += Environment.NewLine
            Next

            Dim SqlCmd As String = "UPDATE " + ToTable + " t" +
                " INNER JOIN " + FromTable + " f" +
                " ON t.NPI=f.NPI " +
                " SET " + UpdateFields

            Debug.WriteLine(SqlCmd)

            Return PerformSQLcommand(SqlCmd)
        End Function

        Function TruncateTable(tableName As String) As Boolean
            Dim strSQL As String = "TRUNCATE TABLE " + tableName
            Console.WriteLine(strSQL)
            Return PerformSQLcommand(strSQL)
        End Function


        Function RenameTable(FromTableName As String, ToTableName As String) As Boolean
            Dim strSQL As String = "RENAME " + FromTableName + " TO " + ToTableName
            Console.WriteLine(strSQL)
            Dim RenameWorked As Boolean = PerformSQLcommand(strSQL)
            Return RenameWorked
        End Function

        Function DoesTableExist(TableName As String) As Boolean
            Dim strSQL As String = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.Tables WHERE TABLE_NAME = '" + TableName + "'"
            Dim tableExists As Boolean
            Using con As New MySqlConnection(SconnStr),
                sqlQuery As New MySqlCommand(strSQL, con)
                con.Open()
                tableExists = Convert.ToBoolean(sqlQuery.ExecuteScalar()) ' returns true if non-zero.
                con.Close()
            End Using
            Return tableExists
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            ' TODO
            ' conn = Nothing
        End Sub

    End Class
End Module