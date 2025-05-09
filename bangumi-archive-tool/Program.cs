﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BangumiArchiveTool;

class Program
{
    static async Task Main(string[] args)
    {
        using var dbContext = new BangumiArchiveDbContext();
        dbContext.Database.Migrate();
        var createDb = new BangumiArchiveDatabaseFunctions();
        try
        {
            createDb.GithubAccessToken = args[0];
        }
        catch (IndexOutOfRangeException)
        {
            Console.WriteLine("No Github Access Token provided.");
            createDb.GithubAccessToken = null;
        }

        Directory.CreateDirectory("temp");
        var dbConnection = dbContext.Database.GetDbConnection();
        var dbPath = dbConnection.ConnectionString.Replace("Data Source=", "");
        Console.WriteLine(Path.GetFullPath(dbPath));
        var result = await createDb.CreateArchiveDatabase(dbContext);
        Console.WriteLine(result);
    }
}

public class BangumiArchiveDatabaseFunctions
{
    private const string _version_ = "1.0.0";

    public BangumiArchiveDatabaseFunctions()
    {
        GithubAccessToken = null;
    }

    public string? GithubAccessToken
    {
        get => _githubAccessToken;
        set => _githubAccessToken = value;
    }

    private string? _githubAccessToken;

    public class Result
    {
        public bool IsSuccess { get; set; }
        public string? DatabasePath { get; set; }
        public string? ExceptionMessage { get; set; }
    }

    public async Task<Result> CreateArchiveDatabase(BangumiArchiveDbContext bangumiArchiveDbContext)
    {
        string tempManifestPath = "temp/manifest.json";
        string tempZipPath = "temp/archive.zip";
        string tempExtractPath = "temp/extracted";

        var fetchingManifestFileClient = new HttpClient();
        fetchingManifestFileClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Anitou_Database/{_version_}");
        if (!string.IsNullOrEmpty(_githubAccessToken))
        {
            fetchingManifestFileClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _githubAccessToken);
        }

        Console.WriteLine("Downloading official manifest file.");
        var manifestRequest = new HttpRequestMessage(HttpMethod.Get,
            "https://raw.githubusercontent.com/bangumi/Archive/refs/heads/master/aux/latest.json");
        HttpResponseMessage? manifestResponse;
        try
        {
            manifestResponse = await fetchingManifestFileClient.SendAsync(manifestRequest);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: Failed to download manifest file: " + e.Message);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage = "Error: Failed to download manifest file: " + e.Message
            };
        }

        if (!manifestResponse.IsSuccessStatusCode)
        {
            Console.WriteLine("Error: " + manifestResponse.StatusCode + ": " +
                              manifestResponse.Content.ReadAsStringAsync().Result);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage = "Error: " + manifestResponse.StatusCode + ": " +
                                   manifestResponse.Content.ReadAsStringAsync().Result
            };
        }

        Console.WriteLine("Successfully downloaded official manifest file.");
        Console.WriteLine("Deserializing manifest file.");
        var jsonDeserializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var responseBody = await manifestResponse.Content.ReadAsStringAsync();
        BangumiArchiveDatabaseModels.ArchiveDatabaseInfo? manifestResponseBodyDeserialized;
        try
        {
            manifestResponseBodyDeserialized =
                JsonSerializer.Deserialize<BangumiArchiveDatabaseModels.ArchiveDatabaseInfo>(responseBody,
                    jsonDeserializerOptions);
            bangumiArchiveDbContext.ArchiveDatabaseInfo.Add(manifestResponseBodyDeserialized);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: Failed to deserialize or storage manifest file: " + e.Message);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage = "Error: Failed to deserialize or storage manifest file: " + e.Message
            };
        }

        var remoteZipUrl = manifestResponseBodyDeserialized.Url;
        Console.WriteLine("Successfully deserialized and storaged manifest file.");
        Console.WriteLine("Downloading official archived data zip file.");
        var zipFileRequest = new HttpRequestMessage(HttpMethod.Get, remoteZipUrl);
        zipFileRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            var fetchingRawDataZipFileClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
                DefaultRequestHeaders =
                {
                    UserAgent = { ProductInfoHeaderValue.Parse($"Anitou_Database/{_version_}") }
                }
            };
            if (!string.IsNullOrEmpty(_githubAccessToken))
            {
                fetchingRawDataZipFileClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _githubAccessToken);
            }

            using var zipFileResponse = await fetchingRawDataZipFileClient.SendAsync(zipFileRequest);
            if (!zipFileResponse.IsSuccessStatusCode)
            {
                if (zipFileResponse.StatusCode == HttpStatusCode.Redirect)
                {
                    var redirectUrl = zipFileResponse.Headers.Location;
                    var downloadRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                    using var downloadResponse = await fetchingRawDataZipFileClient.SendAsync(downloadRequest);
                    if (!downloadResponse.IsSuccessStatusCode)
                    {
                        GC.Collect();
                        Console.WriteLine("Error: Failed to download file(s): " + downloadResponse.StatusCode + ": " +
                                          downloadResponse.Content.ReadAsStringAsync().Result);
                        return new Result
                        {
                            IsSuccess = false,
                            ExceptionMessage = "Error: " + downloadResponse.StatusCode + ": " +
                                               downloadResponse.Content.ReadAsStringAsync().Result
                        };
                    }

                    await using var zipStream = await downloadResponse.Content.ReadAsStreamAsync();
                    await using var fileStream = File.Create(tempZipPath);
                    await zipStream.CopyToAsync(fileStream);
                }
                else
                {
                    GC.Collect();
                    Console.WriteLine("Error: Failed to download file(s): " + zipFileResponse.StatusCode + ": " +
                                      zipFileResponse.Content.ReadAsStringAsync().Result);
                    return new Result
                    {
                        IsSuccess = false,
                        ExceptionMessage = "Error: " + zipFileResponse.StatusCode + ": " +
                                           zipFileResponse.Content.ReadAsStringAsync().Result
                    };
                }
            }
            else
            {
                GC.Collect();
                await using var zipStream = await zipFileResponse.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(tempZipPath);
                await zipStream.CopyToAsync(fileStream);
            }
        }
        catch (Exception e)
        {
            GC.Collect();
            Console.WriteLine("Error: Failed to download file(s): " + e.Message);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage = "Error: Failed to download file(s): " + e.Message
            };
        }

        Console.WriteLine("Successfully downloaded official archived data zip file.");
        Console.WriteLine("Extracting zip file.");

        try
        {
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);
        }
        catch (Exception e)
        {
            GC.Collect();
            Console.WriteLine("Error: Failed to extract zip file: " + e.Message);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage = "Error: Failed to extract zip file: " + e.Message
            };
        }

        GC.Collect();
        Console.WriteLine("Successfully extracted zip file.");
        Console.WriteLine("Deserializing raw data. This may take a while.");

        string[] jsonFiles;
        try
        {
            jsonFiles = Directory.GetFiles(tempExtractPath, "*.jsonlines");
        }
        catch (Exception e)
        {
            GC.Collect();
            Console.WriteLine(
                "Error: Failed to read extracted json files. Is there any permission issue with temp folders? Details: " +
                e.Message);
            return new Result
            {
                IsSuccess = false,
                ExceptionMessage =
                    "Error: Failed to read extracted json files. Is there any permission issue with temp folders? Details: " +
                    e.Message
            };
        }

        foreach (var jsonFile in jsonFiles)
        {
            Console.WriteLine("Deserializing file: " + jsonFile);
            try
            {
                var fileNameLower = Path.GetFileNameWithoutExtension(jsonFile).ToLower();
                switch (fileNameLower)
                {
                    case "character":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.Character>(bangumiArchiveDbContext,
                            jsonFile, jsonDeserializerOptions);
                        break;
                    case "episode":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.Episode>(bangumiArchiveDbContext,
                            jsonFile, jsonDeserializerOptions);
                        break;
                    case "person":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.Person>(bangumiArchiveDbContext,
                            jsonFile, jsonDeserializerOptions);
                        break;
                    case "person-characters":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.PersonCharacter>(
                            bangumiArchiveDbContext, jsonFile, jsonDeserializerOptions);
                        break;
                    case "subject":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.Subject>(bangumiArchiveDbContext,
                            jsonFile, jsonDeserializerOptions);
                        break;
                    case "subject-characters":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.SubjectCharacter>(
                            bangumiArchiveDbContext, jsonFile, jsonDeserializerOptions);
                        break;
                    case "subject-persons":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.SubjectPerson>(
                            bangumiArchiveDbContext, jsonFile, jsonDeserializerOptions);
                        break;
                    case "subject-relations":
                        await DeserializeOfficialFile<BangumiArchiveDatabaseModels.SubjectRelation>(
                            bangumiArchiveDbContext, jsonFile, jsonDeserializerOptions);
                        break;
                    default:
                        Console.WriteLine("Warning: Unknown file: " + jsonFile + ". Skipping.");
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: Failed to deserialize file ({jsonFile}): {e}");
                Console.WriteLine("This file will be skipped. Continuing.");
                throw e;
            }
            finally
            {
                GC.Collect();
            }

            Console.WriteLine($"Finished deserializing file: {jsonFile}.");
            GC.Collect();
        }

        Console.WriteLine("Deserialization completed.");
        Console.WriteLine("Cleaning up temporary files.");
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (File.Exists(tempManifestPath))
                File.Delete(tempManifestPath);
            if (File.Exists(tempZipPath))
                File.Delete(tempZipPath);
            if (Directory.Exists(tempExtractPath))
                Directory.Delete(tempExtractPath, true);
        }
        catch (IOException)
        {
            Console.WriteLine("Warning: Failed to delete temporary files. Please delete them manually.");
        }

        return new Result { IsSuccess = true };
    }

    private static async Task DeserializeOfficialFile<T>(BangumiArchiveDbContext officialSubjectDbContext,
        string jsonFile, JsonSerializerOptions jsonDeserializerOptions)
    {
        const int maxLines = 200000;
        await using var fileStream = File.OpenRead(jsonFile);
        using var streamReader = new StreamReader(fileStream);
        var lines = new List<string>(maxLines);
        int lineCount = 1;
        while (!streamReader.EndOfStream)
        {
            lines.Clear();
            for (int i = 0; i < maxLines && !streamReader.EndOfStream; i++)
            {
                var line = await streamReader.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                    lines.Add(line);
            }

            if (lines.Count == 0) continue;

            int batchSize = Math.Max(1, lines.Count / 20000);
            var batches = lines
                .Select((line, index) => new { line, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.line).ToList())
                .ToList();

            var deserializedRecords = new List<T>[batches.Count];
            var tasks = batches.Select(async (batch, index) =>
            {
                var records = new List<T>();
                foreach (var line in batch)
                {
                    T record;
                    try
                    {
                        record = JsonSerializer.Deserialize<T>(line, jsonDeserializerOptions);
                        if (record is BangumiArchiveDatabaseModels.Subject subject)
                            subject.Id = lineCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(line);
                        throw ex;
                    }

                    if (record != null)
                        records.Add(record);
                }

                deserializedRecords[index] = records;
            });

            await Task.WhenAll(tasks);

            foreach (var records in deserializedRecords)
            {
                foreach (var record in records)
                {
                    switch (record)
                    {
                        case BangumiArchiveDatabaseModels.Character c:
                            await officialSubjectDbContext.Character.AddAsync(c);
                            break;
                        case BangumiArchiveDatabaseModels.Episode e:
                            await officialSubjectDbContext.Episode.AddAsync(e);
                            break;
                        case BangumiArchiveDatabaseModels.Person p:
                            await officialSubjectDbContext.Person.AddAsync(p);
                            break;
                        case BangumiArchiveDatabaseModels.PersonCharacter pc:
                            await officialSubjectDbContext.PersonCharacter.AddAsync(pc);
                            break;
                        case BangumiArchiveDatabaseModels.Subject s:
                            await officialSubjectDbContext.Subject.AddAsync(s);
                            break;
                        case BangumiArchiveDatabaseModels.SubjectCharacter sc:
                            await officialSubjectDbContext.SubjectCharacter.AddAsync(sc);
                            break;
                        case BangumiArchiveDatabaseModels.SubjectPerson sp:
                            await officialSubjectDbContext.SubjectPerson.AddAsync(sp);
                            break;
                        case BangumiArchiveDatabaseModels.SubjectRelation sr:
                            await officialSubjectDbContext.SubjectRelation.AddAsync(sr);
                            break;
                    }
                }
            }

            await officialSubjectDbContext.SaveChangesAsync();
            GC.Collect();
        }
    }
}

public class BangumiArchiveDatabaseModels
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public class ArchiveDatabaseInfo
    {
        public string Browser_Download_Url { get; set; }
        public string Content_Type { get; set; }
        public string Created_At { get; set; }
        [Key] public int Id { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Node_Id { get; set; }
        public int Size { get; set; }
        public string Updated_At { get; set; }
        public string Url { get; set; }
    }

    public class Subject
    {
        [Key] public int Id { get; set; }
        public int Type { get; set; }
        public string Name { get; set; }
        public string Name_Cn { get; set; }
        public string InfoBox { get; set; }
        public int Platform { get; set; }
        public string Summary { get; set; }
        public bool NSFW { get; set; }

        [NotMapped]
        public List<TagItem> Tags
        {
            get => JsonSerializer.Deserialize<List<TagItem>>(Tags_Json ?? "[]", JsonOptions);
            set => Score_Details_Json = JsonSerializer.Serialize(value, JsonOptions);
        }

        public string Tags_Json { get; set; } = "[]";

        [NotMapped]
        public class TagItem
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        public double Score { get; set; }

        [NotMapped]
        public Dictionary<string, int> Score_Details
        {
            get => JsonSerializer.Deserialize<Dictionary<string, int>>(Score_Details_Json ?? "{}");
            set => Score_Details_Json = JsonSerializer.Serialize(value);
        }

        public string Score_Details_Json { get; set; }

        public int Rank { get; set; }
        public string Date { get; set; }

        [NotMapped]
        public Dictionary<string, int> Favorite
        {
            get => JsonSerializer.Deserialize<Dictionary<string, int>>(Favorite_Json ?? "{}");
            set => Favorite_Json = JsonSerializer.Serialize(value);
        }

        public string Favorite_Json { get; set; }

        public bool Series { get; set; }
    }

    public class Character
    {
        [Key] public int Id { get; set; }
        public int Role { get; set; }
        public string Name { get; set; }
        public string InfoBox { get; set; }
        public string Summary { get; set; }
        public int Comments { get; set; }
        public int Collects { get; set; }
    }

    public class Episode
    {
        [Key] public int Id { get; set; }
        public string Name { get; set; }
        public string Name_Cn { get; set; }
        public string Description { get; set; }
        public string AirDate { get; set; }
        public int Disc { get; set; }
        public string Duration { get; set; }
        public int Subject_Id { get; set; }
        public decimal Sort { get; set; }
        public int Type { get; set; }
    }

    public class Person
    {
        [Key] public int Id { get; set; }
        public string Name { get; set; }
        public int Type { get; set; }
        public List<string>? Carrer { get; set; }
        public string InfoBox { get; set; }
        public string Summary { get; set; }
        public int Comments { get; set; }
        public int Collects { get; set; }
    }

    public class PersonCharacter
    {
        public int Person_Id { get; set; }
        public int Subject_Id { get; set; }
        public int Character_Id { get; set; }
        public string Summary { get; set; }
    }

    public class SubjectCharacter
    {
        public int Character_Id { get; set; }
        public int Subject_Id { get; set; }
        public int Type { get; set; }
        public int Order { get; set; }
    }

    public class SubjectPerson
    {
        public int Person_Id { get; set; }
        public int Subject_Id { get; set; }
        public int Position { get; set; }
    }

    public class SubjectRelation
    {
        [Key] public int RelationId { get; set; }
        public int Subject_Id { get; set; }
        public int Relation_Type { get; set; }
        public int Related_Subject_Id { get; set; }
        public int Order { get; set; }
    }
}

public class BangumiArchiveDbContext : DbContext
{
    private readonly string _databasePath;

    public BangumiArchiveDbContext()
    {
        _databasePath = "BangumiArchive.db";
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_databasePath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BangumiArchiveDatabaseModels.PersonCharacter>()
            .HasKey(pc => new { pc.Person_Id, pc.Subject_Id, pc.Character_Id });
        modelBuilder.Entity<BangumiArchiveDatabaseModels.SubjectCharacter>()
            .HasKey(sc => new { sc.Character_Id, sc.Subject_Id });
        modelBuilder.Entity<BangumiArchiveDatabaseModels.SubjectPerson>()
            .HasKey(sp => new { sp.Subject_Id, sp.Person_Id, sp.Position });
    }

    public BangumiArchiveDbContext(DbContextOptions<BangumiArchiveDbContext> options) : base(options)
    {
    }

    public DbSet<BangumiArchiveDatabaseModels.ArchiveDatabaseInfo> ArchiveDatabaseInfo { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.Subject> Subject { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.Episode> Episode { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.Character> Character { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.Person> Person { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.PersonCharacter> PersonCharacter { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.SubjectCharacter> SubjectCharacter { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.SubjectPerson> SubjectPerson { get; set; }
    public DbSet<BangumiArchiveDatabaseModels.SubjectRelation> SubjectRelation { get; set; }
}