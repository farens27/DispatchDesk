using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddHostedService<QueueWorker>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/jobs", (QueueStore q) => q.Snapshot());
app.MapPost("/api/jobs", (Submit input, QueueStore q) => {
    if (input.Kind is not ("report" or "unstable" or "broken") || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80)
        return Results.BadRequest(new { error = "Name required (1–80 characters); kind: report, unstable, broken." });
    try { return q.Add(input) is { } job ? Results.Created($"api/jobs/{job.Id}", job) : Results.Json(new { error = "Demo full: maximum 100 jobs, 20 active. Wait for worker." }, statusCode: 429); }
    catch (IOException) { return Results.Problem("Persistence unavailable. Job not accepted."); }
});
app.MapPost("/api/jobs/{id:guid}/retry", (Guid id, QueueStore q) => {
    try { return q.Retry(id) ? Results.Ok() : Results.Conflict(new { error = "Only dead-letter jobs can retry; queue must have capacity." }); }
    catch (IOException) { return Results.Problem("Persistence unavailable. Retry not accepted."); }
});
app.Run();

record Submit(string Name, string Kind);
record Job(Guid Id, string Name, string Kind, string Status, int Attempts, DateTimeOffset Created, DateTimeOffset NextRun, string? Error);
sealed class QueueStore {
    readonly object gate = new();
    readonly string path = Environment.GetEnvironmentVariable("DISPATCH_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dispatchdesk", "jobs.json");
    List<Job> jobs;
    public QueueStore() {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // ponytail: single-process file store; use transactional DB and leases for multiple workers.
        jobs = File.Exists(path) ? JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid queue data") : new();
        jobs = jobs.Select(j => j.Status == "processing" ? j with { Status = "queued", NextRun = DateTimeOffset.UtcNow } : j).ToList();
    }
    void Commit(List<Job> next) {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next));
        File.Move(temp, path, true);
        jobs = next;
    }
    public Job[] Snapshot() { lock (gate) return jobs.OrderByDescending(j => j.Created).ToArray(); }
    public Job? Add(Submit input) {
        lock (gate) {
            if (jobs.Count(j => j.Status is "queued" or "processing" or "retrying") >= 20) return null;
            var next = new List<Job>(jobs);
            if (next.Count >= 100) {
                var oldest = next.Where(j => j.Status == "completed").OrderBy(j => j.Created).FirstOrDefault();
                if (oldest is null) return null;
                next.Remove(oldest);
            }
            var job = new Job(Guid.NewGuid(), input.Name.Trim(), input.Kind, "queued", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
            next.Add(job); Commit(next); return job;
        }
    }
    public bool Retry(Guid id) {
        lock (gate) {
            var index = jobs.FindIndex(j => j.Id == id && j.Status == "dead-letter");
            if (index < 0 || jobs.Count(j => j.Status is "queued" or "processing" or "retrying") >= 20) return false;
            var next = new List<Job>(jobs);
            next[index] = next[index] with { Status = "queued", Attempts = 0, Error = null, NextRun = DateTimeOffset.UtcNow };
            Commit(next); return true;
        }
    }
    public Job? Claim() {
        lock (gate) {
            var index = jobs.FindIndex(j => (j.Status is "queued" or "retrying") && j.NextRun <= DateTimeOffset.UtcNow);
            if (index < 0) return null;
            var next = new List<Job>(jobs);
            next[index] = next[index] with { Status = "processing", Attempts = next[index].Attempts + 1 };
            Commit(next); return next[index];
        }
    }
    public void Finish(Job job, bool success) {
        lock (gate) {
            var next = new List<Job>(jobs);
            var i = next.FindIndex(j => j.Id == job.Id);
            next[i] = job with {
                Status = success ? "completed" : job.Attempts >= 3 ? "dead-letter" : "retrying",
                Error = success ? null : "Simulated handler failure",
                NextRun = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, job.Attempts))
            };
            Commit(next);
        }
    }
}
sealed class QueueWorker(QueueStore queue, ILogger<QueueWorker> log) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                var job = queue.Claim();
                if (job is null) { await Task.Delay(500, token); continue; }
                // Cancellation leaves processing persisted; restart requeues for at-least-once delivery.
                await Task.Delay(1500, token);
                var success = job.Kind == "report" || (job.Kind == "unstable" && job.Attempts >= 2);
                queue.Finish(job, success);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) {
                log.LogCritical(ex, "Worker stopped to avoid processing after persistence failure");
                Environment.ExitCode = 1;
                throw;
            }
        }
    }
}
