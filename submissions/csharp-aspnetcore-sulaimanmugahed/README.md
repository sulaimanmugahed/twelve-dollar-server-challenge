# C# + ASP.NET Core 10

| | |
|---|---|
| Language | C# 14 on .NET SDK **10.0.100** |
| Framework / server | ASP.NET Core 10 Minimal APIs on Kestrel |
| SQLite driver | `Microsoft.Data.Sqlite` **10.0.0** (`SQLitePCLRaw` bundled native SQLite) |
| JSON | `System.Text.Json` from .NET 10 |
| **Nginx or direct** | **Direct**: Kestrel listens on the supplied `HOST:PORT` (normally `0.0.0.0:80`) |

## Running locally

```bash
sudo bash install.sh
bash build.sh
SQLITE_PATH=/path/to/feed.db JWT_SECRET=twelve-dollar-challenge HOST=127.0.0.1 PORT=3000 bash start.sh
```

From the repository root, the full correctness run is:

```bash
bash test/run.sh submissions/csharp-aspnetcore-sulaimanmugahed
```

## Implementation notes

- The app is a single direct Kestrel process. It uses HTTP/1.1 only, permits 20,000 concurrent
  connections, and keeps idle HTTP connections for 75 seconds. This avoids spending the one CPU on
  an additional Nginx hop while still satisfying the direct-server connection requirement.
- The database is one process-local `Microsoft.Data.Sqlite` connection configured for WAL,
  `synchronous=NORMAL`, a five-second busy timeout, a 1 GiB SQLite mmap limit, and a 64 MiB SQLite
  page cache. Each write finishes its SQLite statement before the corresponding 201 response is
  generated, so it is durable at response time.
- SQLite connection/command access is serialized by a short lock. The benchmark VM has one vCPU;
  this prevents concurrent command use and SQLite write-lock contention without adding worker or
  connection-pool overhead. Every feed, post, health, and like request executes its query against
  SQLite while being served—there is no response or query-result cache.
- The hot SQL commands are created once and parameterized. Feed and post queries read the current
  like count using the schema's existing `likes_post_id_idx`; likes use one conditional insert and
  only check post existence after an insert conflict/no-op.
- JWTs are verified manually for every authenticated request using HS256 and constant-time
  comparison. The implementation only accepts `alg: HS256`, validates `exp` (and `nbf` when
  supplied), and separately reports malformed signatures versus invalid `sub`/`username` payloads
  as required by the API specification.
- Responses use compact `System.Text.Json` bytes with explicit property names and order. The post
  body uses ECMAScript-compatible trimming and Unicode scalar counting before it reaches SQLite.

## License

MIT, under the repository [license](../../LICENSE).
