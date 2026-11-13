```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13620H 2.40GHz, 1 CPU, 16 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                | Mean      | Error     | StdDev    | Median    | Gen0   | Allocated |
|-------------------------------------- |----------:|----------:|----------:|----------:|-------:|----------:|
| &#39;parse EXPLAIN JSON (14 KB, 9 nodes)&#39; | 51.196 μs | 1.3498 μs | 3.9588 μs | 51.452 μs | 0.6104 |   7.89 KB |
| &#39;fingerprint plan&#39;                    |  1.033 μs | 0.0334 μs | 0.0959 μs |  1.019 μs | 0.2346 |   2.89 KB |
| &#39;diff two plans&#39;                      |  2.689 μs | 0.1540 μs | 0.4541 μs |  2.669 μs | 0.5875 |   7.21 KB |
