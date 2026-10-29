# Golan Download Manager (GDM) HTTP Engine Benchmark Results (`tests/bench`)

## Environment & Methodology
- **Runtime**: `.NET 10.0.13` on `Microsoft Windows 10.0.26300` (X64)
- **Logical CPU Cores**: `8`
- **Payload Size**: `64 MB` (`67,108,864` bytes) deterministic in-memory stream over loopback (`127.0.0.1`) with HTTP Range (`206 Partial Content`)
- **Iterations per Tier**: `3` (after 1 warm-up pass; median reported)
- **Buffer & I/O Strategy**: Pooled `ArrayPool<byte>.Shared` buffers (`64 KB` per active segment worker) + single shared `SafeFileHandle` (`RandomAccess.WriteAsync`) + reusable timeout `CancellationTokenSource`

## Localhost Multi-Connection Throughput, CPU & Allocations

| Connections | Throughput (MB/s) | Wall Time (ms) | CPU (%) | Allocations (KB / MB) | Allocations (MB / MB) | SHA-256 Verified |
|------------:|------------------:|---------------:|--------:|----------------------:|----------------------:|:----------------:|
| 1 | **594.7 MB/s** | 107.6 ms | 14.5% | 3.07 KB/MB | 0.0030 MB/MB | PASS |
| 4 | **344.1 MB/s** | 186.0 ms | 12.7% | 5.20 KB/MB | 0.0051 MB/MB | PASS |
| 8 | **435.3 MB/s** | 147.0 ms | 23.5% | 6.00 KB/MB | 0.0059 MB/MB | PASS |
| 16 | **403.6 MB/s** | 158.6 ms | 17.8% | 7.83 KB/MB | 0.0076 MB/MB | PASS |
| 32 | **164.0 MB/s** | 390.2 ms | 11.7% | 19.71 KB/MB | 0.0192 MB/MB | PASS |

## Notes
- **Allocation & I/O efficiency**: Segment and single-stream workers rent their `64 KB` read buffers from `ArrayPool<byte>.Shared` (returned in `finally`), write concurrently to a single shared `SafeFileHandle` via `RandomAccess.WriteAsync`, and reuse a single linked `CancellationTokenSource` per stream in `ReadWithTimeoutAsync`, keeping managed heap allocations to `3.07–19.71 KB/MB` (`0.0030–0.0192 MB/MB`).
- **Opt-in internet benchmark**: Run `dotnet run --project GDM/tests/bench/Bench.csproj -c Release -- --internet` to execute an optional smoke benchmark against public speed-test endpoints (`proof.ovh.net` / `speed.hetzner.de`). Not executed by default.
