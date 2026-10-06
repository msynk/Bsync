```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | Count | Store       | Mean      | Error      | StdDev    | P95       | Gen0       | Gen1       | Gen2      | Allocated |
|-------------- |------ |------------ |----------:|-----------:|----------:|----------:|-----------:|-----------:|----------:|----------:|
| **OrderedPage**   | **10000** | **memory**      |  **30.17 ms** |  **38.619 ms** |  **2.117 ms** |  **32.21 ms** |  **2718.7500** |  **2593.7500** |  **750.0000** |  **23.73 MB** |
| SelectivePage | 10000 | memory      |  13.89 ms |   7.712 ms |  0.423 ms |  14.17 ms |  1281.2500 |   765.6250 |         - |  15.43 MB |
| **OrderedPage**   | **10000** | **sqlite-full** |  **57.92 ms** |  **49.402 ms** |  **2.708 ms** |  **60.57 ms** |  **4454.5455** |  **2727.2727** | **1000.0000** |  **43.71 MB** |
| SelectivePage | 10000 | sqlite-full |  23.50 ms |  54.722 ms |  2.999 ms |  25.60 ms |  1812.5000 |  1750.0000 |  468.7500 |  21.81 MB |
| **OrderedPage**   | **50000** | **memory**      | **313.36 ms** | **115.030 ms** |  **6.305 ms** | **317.84 ms** | **13000.0000** | **12500.0000** | **3500.0000** | **119.06 MB** |
| SelectivePage | 50000 | memory      | 115.57 ms |  58.700 ms |  3.218 ms | 117.76 ms |   666.6667 |          - |         - |  34.67 MB |
| **OrderedPage**   | **50000** | **sqlite-full** | **337.10 ms** | **573.430 ms** | **31.432 ms** | **365.32 ms** | **22333.3333** | **13333.3333** | **4333.3333** | **218.51 MB** |
| SelectivePage | 50000 | sqlite-full |  20.28 ms |  39.118 ms |  2.144 ms |  22.34 ms |  1812.5000 |  1750.0000 |  453.1250 |  21.81 MB |
