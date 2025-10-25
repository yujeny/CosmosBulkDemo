using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using CosmosBulkDemo.Models;

record AppConfig(CosmosConfig Cosmos, LoadConfig Load);
record CosmosConfig(string Endpoint, string Key, string Database, string Container);
record LoadConfig(int Docs, int Concurrency, int Users);

class Program
{
    static async Task<int> Main()
    {
        var config = LoadConfigFromEnvOrFile();

        var clientOptions = new CosmosClientOptions
        {
            AllowBulkExecution = true,            // 🔥 대량 적재 최적화 파이프라인
            ApplicationName = "CosmosBulkDemo"
        };

        using var client = new CosmosClient(
            Get("COSMOS_ENDPOINT", config.Cosmos.Endpoint),
            Get("COSMOS_KEY", config.Cosmos.Key),
            clientOptions);

        var db = client.GetDatabase(Get("COSMOS_DB", config.Cosmos.Database));
        var container = db.GetContainer(Get("COSMOS_CONTAINER", config.Cosmos.Container));

        Console.WriteLine("== Cosmos Bulk Demo ==");
        Console.WriteLine($"DB: {db.Id}, Container: {container.Id}");

        // 로드 파라미터
        var totalDocs   = int.Parse(Get("LOAD_DOCS",         config.Load.Docs.ToString()));
        var concurrency = int.Parse(Get("LOAD_CONCURRENCY",  config.Load.Concurrency.ToString()));
        var users       = int.Parse(Get("LOAD_USERS",        config.Load.Users.ToString()));

        // 1) 대량 적재 (Bulk Upsert)
        Console.WriteLine($"\n[1] Bulk Upsert: docs={totalDocs}, concurrency={concurrency}, users={users}");
        var (inserted, ruInsert, tInsert) = await BulkUpsertAsync(container, totalDocs, users, concurrency);
        Console.WriteLine($"Inserted={inserted}, RU≈{ruInsert:F2}, Elapsed={tInsert.TotalSeconds:F1}s, RU/s≈{ruInsert/Math.Max(1, tInsert.TotalSeconds):F1}");

        // 2) 배치 읽기 – ReadMany (id+pk 다건 조회, 초저RU)
        Console.WriteLine("\n[2] ReadMany batch read (샘플 500건)");
        var (readManyCount, readManyRU, readManyElapsed) = await ReadManyDemoAsync(container, sample: 500, users);
        Console.WriteLine($"ReadMany={readManyCount}, RU≈{readManyRU:F2}, Elapsed={readManyElapsed.TotalSeconds:F1}s");

        // 3) 파티션키 포함 쿼리 페이징 (RU 효율 쿼리)
        Console.WriteLine("\n[3] Query with PK (TOP 100 x 10 partitions)");
        var (readCount, readRU, tRead) = await QueryWithPkPagedAsync(container, partitions: 10, users);
        Console.WriteLine($"QueryRead={readCount}, RU≈{readRU:F2}, Elapsed={tRead.TotalSeconds:F1}s");

        // 4) TransactionalBatch – 동일 파티션키 원자적 처리
        Console.WriteLine("\n[4] TransactionalBatch (same partition key, 5 items)");
        var batchPk = "user-000001";
        var batchRU = await TransactionalBatchDemoAsync(container, batchPk);
        Console.WriteLine($"TransactionalBatch RU≈{batchRU:F2}");

        Console.WriteLine("\nDone.");
        return 0;

        static string Get(string env, string fallback) => Environment.GetEnvironmentVariable(env) ?? fallback;
    }

    static AppConfig LoadConfigFromEnvOrFile()
    {
        CosmosConfig cosmos = new(
            Endpoint: "https://<your-account>.documents.azure.com:443/",
            Key: "<your-key>",
            Database: "demoDB",
            Container: "orders"
        );
        LoadConfig load = new(Docs: 10000, Concurrency: 32, Users: 2000);

        try
        {
            if (File.Exists("appsettings.json"))
            {
                var json = File.ReadAllText("appsettings.json");
                var root = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (root is not null)
                {
                    cosmos = root.Cosmos ?? cosmos;
                    load = root.Load ?? load;
                }
            }
        }
        catch { /* ignore */ }

        return new AppConfig(cosmos, load);
    }

    static async Task<(int count, double ru, TimeSpan elapsed)> BulkUpsertAsync(
        Container container, int total, int users, int concurrency)
    {
        var rnd = new Random(42);
        var docs = Enumerable.Range(0, total).Select(i =>
        {
            var uidNum = rnd.Next(1, users + 1);
            string uid = $"user-{uidNum:D6}";
            return new OrderDoc
            {
                id = Guid.NewGuid().ToString("N"),
                userId = uid,
                orderId = $"{uid}-{i:D8}",
                amount = (decimal)(rnd.NextDouble() * 500 + 10),
                sku = $"SKU-{rnd.Next(1, 9999):D4}",
                ts = DateTime.UtcNow.AddSeconds(-rnd.Next(0, 86400))
            };
        }).ToArray();

        var sw = Stopwatch.StartNew();
        double totalRU = 0;
        object ruLock = new();

        using var sem = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();
        int completed = 0;

        foreach (var d in docs)
        {
            await sem.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var resp = await container.UpsertItemAsync(d, new PartitionKey(d.userId));
                    lock (ruLock) totalRU += resp.RequestCharge;
                    Interlocked.Increment(ref completed);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // 간단 재시도 (SDK도 백오프 내장, 데모용 보강)
                    await Task.Delay(50);
                    var resp = await container.UpsertItemAsync(d, new PartitionKey(d.userId));
                    lock (ruLock) totalRU += resp.RequestCharge;
                    Interlocked.Increment(ref completed);
                }
                finally
                {
                    sem.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        return (completed, totalRU, sw.Elapsed);
    }

    static async Task<(int count, double ru, TimeSpan elapsed)> ReadManyDemoAsync(
        Container container, int sample, int users)
    {
        // 샘플 (id, pk) 생성: 실제론 보관된 목록에서 가져오는 게 일반적
        var rnd = new Random(7);
        var pairs = new List<(string id, PartitionKey pk)>(sample);
        for (int i = 0; i < sample; i++)
        {
            var uid = $"user-{rnd.Next(1, users + 1):D6}";
            var id = Guid.NewGuid().ToString("N"); // 존재하지 않을 확률 높음(데모)
            pairs.Add((id, new PartitionKey(uid)));
        }

        // 데모를 위해 실제 존재하는 문서도 조금 섞기: user-000001에 최신 5건
        var pk = new PartitionKey("user-000001");
        var q = new QueryDefinition("SELECT TOP 5 c.id FROM c WHERE c.userId=@u ORDER BY c.ts DESC")
            .WithParameter("@u", "user-000001");

        var existing = new List<string>();
        var it = container.GetItemQueryIterator<dynamic>(q, requestOptions: new QueryRequestOptions { PartitionKey = pk });
        while (it.HasMoreResults)
        {
            var page = await it.ReadNextAsync();
            foreach (var item in page) existing.Add((string)item.id);
            break;
        }
        for (int i = 0; i < Math.Min(5, existing.Count) && i < pairs.Count; i++)
        {
            pairs[i] = (existing[i], pk);
        }

        var sw = Stopwatch.StartNew();
        double totalRU = 0;
        int hit = 0;

        var resp = await container.ReadManyItemsAsync<dynamic>(pairs);
        foreach (var r in resp)
        {
            if (r.StatusCode == System.Net.HttpStatusCode.OK) hit++;
        }
        totalRU += resp.RequestCharge;

        sw.Stop();
        return (hit, totalRU, sw.Elapsed);
    }

    static async Task<(int count, double ru, TimeSpan elapsed)> QueryWithPkPagedAsync(
        Container container, int partitions, int users)
    {
        var rnd = new Random(11);
        var sw = Stopwatch.StartNew();
        double totalRU = 0;
        int totalRead = 0;

        for (int i = 0; i < partitions; i++)
        {
            var uid = $"user-{rnd.Next(1, users + 1):D6}";
            var q = new QueryDefinition(
                "SELECT TOP 100 c.id, c.userId, c.orderId, c.amount, c.ts FROM c WHERE c.userId = @u ORDER BY c.ts DESC"
            ).WithParameter("@u", uid);

            var it = container.GetItemQueryIterator<OrderDoc>(
                q,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(uid), MaxItemCount = 100 });

            while (it.HasMoreResults)
            {
                var page = await it.ReadNextAsync();
                totalRU += page.RequestCharge;
                totalRead += page.Count;
            }
        }

        sw.Stop();
        return (totalRead, totalRU, sw.Elapsed);
    }

    static async Task<double> TransactionalBatchDemoAsync(Container container, string userId)
    {
        var batch = container.CreateTransactionalBatch(new PartitionKey(userId));
        for (int i = 0; i < 5; i++)
        {
            batch.CreateItem(new OrderDoc
            {
                id = Guid.NewGuid().ToString("N"),
                userId = userId,
                orderId = $"{userId}-BATCH-{i:D2}",
                amount = 100 + i,
                sku = $"SKU-BATCH-{i:D2}",
                ts = DateTime.UtcNow
            });
        }
        var resp = await batch.ExecuteAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"TransactionalBatch failed: {resp.StatusCode}");
        }
        return resp.RequestCharge;
    }
}
