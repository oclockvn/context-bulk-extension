# Performance Improvement Investigation Plan

This document tracks questions and investigation areas for improving ContextBulkExtension performance.

---

## Architecture & Design Questions

### 1. Metadata Caching Strategy
- **Question**: Why do we cache by `(EntityType, ContextType)` tuple instead of just `EntityType`?
- **Rationale**: Are there scenarios where the same entity behaves differently across different DbContext types?
- **Investigation**:
  - Measure cache hit rate in real-world scenarios
  - Evaluate if `ConcurrentDictionary` is optimal, or if we need size-limited cache (LRU) for applications with many entity types
- **Status**: = To Investigate

### 2. Expression Compilation Performance
- **Current Approach**: Compiled getters created once per entity type using Expression trees
- **Alternatives to Benchmark**:
  - Source generators (compile-time code generation)
  - Cached `PropertyInfo.GetValue()` with fast reflection
  - `Delegate.CreateDelegate()` for direct method calls
- **Questions**:
  - How much overhead does expression compilation add on first use?
  - What's the performance difference between approaches for hot paths?
- **Status**: = To Investigate

### 3. Batch Processing Strategy
- **Current Default**: BatchSize = 10,000
- **Questions**:
  - Was this empirically determined, or based on SQL Server's internal buffering?
  - Do we process all entities in a single batch, or stream in chunks?
  - Could we pipeline work (read next batch while writing current)?
- **Investigation**: Benchmark different batch sizes with varying row counts
- **Status**: = To Investigate

### 4. EntityDataReader Implementation
- **Questions**:
  - How does this perform with very wide tables (100+ columns)?
  - Are we boxing value types when reading properties?
  - Could we use generic methods or `Unsafe.As<T>` to avoid boxing?
  - Do we support async enumeration (`IAsyncEnumerable<T>`) for source data?
- **Status**: = To Investigate

---

## Specific Performance Questions

### 5. Transaction Handling
- **Current**: Automatically participates in existing EF Core transactions
- **Questions**:
  - What's the performance impact of transaction coordination vs. letting SqlBulkCopy manage its own transaction?
  - Could we batch multiple `BulkInsertAsync` calls into a single SqlBulkCopy operation for better throughput?
- **Status**: = To Investigate

### 6. Identity Column Detection
- **Questions**:
  - Does identity detection require accessing EF Core's model metadata every time?
  - Is this metadata access cached at the EF level?
  - Could we pre-compute more during the metadata caching phase?
- **Current Logic**: Detects when property has `ValueGenerated.OnAdd` AND (DefaultValueSql contains "IDENTITY" OR has value generator factory OR is PK with int/long type)
- **Status**: = To Investigate

### 7. String Building & SQL Escaping
- **Questions**:
  - Where exactly do we escape SQL identifiers?
  - Is escaping done once during metadata caching or per-row?
  - Are we using `StringBuilder` or string interpolation for building column lists?
- **Status**: = To Investigate

---

## Edge Cases & Concurrency

### 8. Concurrent Metadata Initialization
- **Scenario**: Two threads call `BulkInsertAsync` with same entity type simultaneously, metadata not cached
- **Question**: Could we have a race condition in `ConcurrentDictionary` initialization?
- **Investigation**: Review thread-safety guarantees, consider `Lazy<T>` or locks if needed
- **Status**: = To Investigate

### 9. Memory Pressure Analysis
- **Questions**:
  - When inserting millions of rows, what's peak memory usage?
  - Do we keep all entities in memory, or can we accept `IAsyncEnumerable<T>` to truly stream from source?
  - How does GC pressure look during large operations?
  - What generation do allocations reach (Gen0, Gen1, Gen2)?
- **Investigation**: Profile with dotMemory/PerfView on large datasets
- **Status**: = To Investigate

### 10. SqlBulkCopy Configuration
- **Questions**:
  - Are we using `SqlBulkCopyOptions.KeepIdentity`, `KeepNulls`, `CheckConstraints`, etc.?
  - Have we benchmarked different `SqlBulkCopyOptions` combinations?
  - What's the impact of `UseTableLock` on concurrent workloads?
- **Investigation**: Test all option combinations with realistic workloads
- **Status**: = To Investigate

---

## Potential Improvement Areas

### High Priority
- [ ] **Parallel Processing**: For very large datasets, partition and insert in parallel
- [ ] **Column Order Optimization**: Match physical table layout for better cache locality
- [ ] **Span<T> and Memory<T>**: Reduce allocations in hot paths
- [ ] **IAsyncEnumerable Support**: True streaming without materializing full dataset

### Medium Priority
- [ ] **Code Generation**: Eliminate runtime expression compilation overhead
- [ ] **Telemetry/Metrics**: Track actual performance in production (execution time, row throughput, memory usage)
- [ ] **Batch Size Auto-tuning**: Dynamically adjust based on row size and available memory
- [ ] **Connection Pool Optimization**: Ensure efficient connection reuse

### Low Priority
- [ ] **Compression**: For large text/binary columns, consider compression before transmission
- [ ] **Custom Value Converters**: Optimize hot path for common conversions
- [ ] **Documentation**: Performance best practices guide

---

## Known Issues & Customer Feedback
- **To be populated**: Track any reported performance bottlenecks or customer complaints

---

## Benchmarking Plan

### Test Scenarios
1. **Small batch**: 1,000 rows, 10 columns
2. **Medium batch**: 100,000 rows, 20 columns
3. **Large batch**: 1,000,000 rows, 10 columns
4. **Wide table**: 10,000 rows, 100+ columns
5. **Concurrent inserts**: Multiple threads inserting simultaneously

### Metrics to Track
- Execution time (total and per-row)
- Memory usage (peak and average)
- GC collections (Gen0, Gen1, Gen2)
- CPU usage
- Network bandwidth utilization
- SQL Server wait stats

### Comparison Baseline
- Current implementation
- EF Core's `AddRange()` + `SaveChanges()`
- Raw ADO.NET SqlBulkCopy
- Dapper or other micro-ORMs

---

## Investigation Log

### [Date] - Investigation Title
- **Findings**:
- **Impact**:
- **Action Items**:

---

*Last Updated: 2025-11-08*
