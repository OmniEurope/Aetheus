// SPDX-License-Identifier: EUPL-1.2
using NUnit.Framework;

// Default to one worker for stable functional browser scenarios; QA can explicitly override worker
// count through NUnit.NumberOfTestWorkers. Fixtures sharing test state remain NonParallelizable.
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(1)]
