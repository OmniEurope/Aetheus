// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Servers;

/// <summary>What the kernel says limited the memory of an out-of-memory kill, from its
/// <c>oom-kill:constraint=</c> line or, without it, from the phrase of its "Killed process" line.</summary>
public enum OomKillKind
{
    /// <summary>Not stated: an agent from before the kinds, or a log without the lines that tell.</summary>
    Unknown = 0,

    /// <summary>The host itself ran out of memory (<c>CONSTRAINT_NONE</c>, "Out of memory:").</summary>
    Global = 1,

    /// <summary>A memory cgroup reached its limit, typically a container's (<c>CONSTRAINT_MEMCG</c>,
    /// "Memory cgroup out of memory:"); the host may still have plenty of free memory.</summary>
    Cgroup = 2,

    /// <summary>The cpuset of the task ran out of memory (<c>CONSTRAINT_CPUSET</c>).</summary>
    Cpuset = 3,

    /// <summary>A NUMA memory policy ran out of memory (<c>CONSTRAINT_MEMORY_POLICY</c>).</summary>
    MemoryPolicy = 4
}
