```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | Count | Store       | Mean     | Error    | StdDev   | P95      | Gen0    | Gen1    | Allocated |
|------------ |------ |------------ |---------:|---------:|---------:|---------:|--------:|--------:|----------:|
| **IndexedPage** | **10000** | **memory**      | **199.2 μs** | **374.9 μs** | **20.55 μs** | **219.3 μs** | **39.5508** | **13.4277** | **484.65 KB** |
| **IndexedPage** | **10000** | **sqlite-full** | **381.2 μs** | **360.3 μs** | **19.75 μs** | **400.4 μs** | **72.2656** | **47.8516** | **895.78 KB** |
| **IndexedPage** | **50000** | **memory**      | **317.5 μs** | **632.6 μs** | **34.67 μs** | **350.7 μs** | **39.5508** | **14.6484** | **485.93 KB** |
| **IndexedPage** | **50000** | **sqlite-full** | **634.6 μs** | **567.7 μs** | **31.12 μs** | **665.3 μs** | **72.2656** | **46.8750** | **897.03 KB** |
