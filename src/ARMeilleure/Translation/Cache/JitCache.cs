using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Unwinding;
using ARMeilleure.Memory;
using ARMeilleure.Native;
using Humanizer;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ARMeilleure.Translation.Cache
{
    static partial class JitCache
    {
        private static readonly int _pageSize = (int)MemoryBlock.GetPageSize();
        private static readonly int _pageMask = _pageSize - 1;

        private const int CodeAlignment = 4; // Bytes.

        // Reserved per region, committed on demand, so this costs address space rather than memory.
        // It is sized to keep heavy titles inside a single region: a second region is correct but
        // exercises far less tested ground, and the unwind tables and entry lists it needs make it
        // the more expensive path. Must stay inside int, since offsets within a region are ints.
        private const int CacheSize = 1024 * 1024 * 1024;

        private static JitCacheInvalidation _jitCacheInvalidator;

        private static readonly List<CacheMemoryAllocator> _cacheAllocators = [];

        // One list per region, index-aligned with _jitRegions and _cacheAllocators. These must not be
        // merged into a single list: an entry is keyed by its offset *within its own region*, so
        // entries from two regions collide at the same offsets and a lookup cannot tell them apart.
        private static readonly List<List<CacheEntry>> _cacheEntries = [];

        private static readonly Lock _lock = new();
        private static bool _initialized;

        private static readonly List<ReservedRegion> _jitRegions = [];
        private static int _activeRegionIndex = 0;

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint FlushInstructionCache(nint hProcess, nint lpAddress, nuint dwSize);

        public static void Initialize(IJitMemoryAllocator allocator)
        {
            lock (_lock)
            {
                if (_initialized)
                {
                    for (int i = 0; i < _jitRegions.Count; i++)
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            JitUnwindWindows.RemoveFunctionTableHandler(_jitRegions[i].Pointer);
                        }

                        _jitRegions[i].Dispose();
                    }

                    _jitRegions.Clear();
                    _cacheAllocators.Clear();
                    _cacheEntries.Clear();
                }
                else
                {
                    _initialized = true;
                }

                _activeRegionIndex = 0;

                ReservedRegion firstRegion = new(allocator, CacheSize);
                _jitRegions.Add(firstRegion);

                CacheMemoryAllocator firstCacheAllocator = new(CacheSize);
                _cacheAllocators.Add(firstCacheAllocator);
                _cacheEntries.Add([]);

                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                {
                    _jitCacheInvalidator = new JitCacheInvalidation(allocator);
                }

                if (OperatingSystem.IsWindows())
                {
                    JitUnwindWindows.InstallFunctionTableHandler(
                        firstRegion.Pointer, CacheSize, firstRegion.Pointer + Allocate(_pageSize)
                    );
                }
            }
        }

        public static nint Map(CompiledFunction func)
        {
            byte[] code = func.Code;

            lock (_lock)
            {
                Debug.Assert(_initialized);

                int funcOffset = Allocate(code.Length);
                ReservedRegion targetRegion = _jitRegions[_activeRegionIndex];
                nint funcPtr = targetRegion.Pointer + funcOffset;

                if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    unsafe
                    {
                        fixed (byte* codePtr = code)
                        {
                            JitSupportDarwin.Copy(funcPtr, (nint)codePtr, (ulong)code.Length);
                        }
                    }
                }
                else
                {
                    ReprotectAsWritable(targetRegion, funcOffset, code.Length);
                    Marshal.Copy(code, 0, funcPtr, code.Length);
                    ReprotectAsExecutable(targetRegion, funcOffset, code.Length);

                    if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    {
                        FlushInstructionCache(Process.GetCurrentProcess().Handle, funcPtr, (nuint)code.Length);
                    }
                    else
                    {
                        _jitCacheInvalidator?.Invalidate(funcPtr, (ulong)code.Length);
                    }
                }

                Add(funcOffset, code.Length, func.UnwindInfo);

                return funcPtr;
            }
        }

        public static void Unmap(nint pointer)
        {
            lock (_lock)
            {
                Debug.Assert(_initialized);

                for (int regionIndex = 0; regionIndex < _jitRegions.Count; regionIndex++)
                {
                    ReservedRegion region = _jitRegions[regionIndex];

                    if (pointer.ToInt64() < region.Pointer.ToInt64() ||
                        pointer.ToInt64() >= (region.Pointer + CacheSize).ToInt64())
                    {
                        continue;
                    }

                    int funcOffset = (int)(pointer.ToInt64() - region.Pointer.ToInt64());

                    // The freed range belongs to the region that holds the pointer. Returning it to
                    // the active region's allocator instead would mark memory free that is still in
                    // use there, and the next translation placed at that offset would overlap live
                    // code -- which shows up much later as a jump into whatever overwrote it.
                    if (TryFind(regionIndex, funcOffset, out CacheEntry entry, out int entryIndex) &&
                        entry.Offset == funcOffset)
                    {
                        _cacheAllocators[regionIndex].Free(funcOffset, AlignCodeSize(entry.Size));
                        _cacheEntries[regionIndex].RemoveAt(entryIndex);
                    }

                    return;
                }
            }
        }

        private static void ReprotectAsWritable(ReservedRegion region, int offset, int size)
        {
            int endOffs = offset + size;
            int regionStart = offset & ~_pageMask;
            int regionEnd = (endOffs + _pageMask) & ~_pageMask;

            region.Block.MapAsRwx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private static void ReprotectAsExecutable(ReservedRegion region, int offset, int size)
        {
            int endOffs = offset + size;
            int regionStart = offset & ~_pageMask;
            int regionEnd = (endOffs + _pageMask) & ~_pageMask;

            region.Block.MapAsRx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private static int Allocate(int codeSize)
        {
            codeSize = AlignCodeSize(codeSize);

            int allocOffset = _cacheAllocators[_activeRegionIndex].Allocate(codeSize);

            if (allocOffset >= 0)
            {
                _jitRegions[_activeRegionIndex].ExpandIfNeeded((ulong)allocOffset + (ulong)codeSize);
                return allocOffset;
            }

            int exhaustedRegion = _activeRegionIndex;
            ReservedRegion newRegion = new(_jitRegions[0].Allocator, CacheSize);
            _jitRegions.Add(newRegion);
            _activeRegionIndex = _jitRegions.Count - 1;

            Logger.Warning?.Print(LogClass.Cpu, $"JIT Cache Region {exhaustedRegion} exhausted, creating new Cache Region {_activeRegionIndex} ({((long)(_activeRegionIndex + 1) * CacheSize).Bytes()} Total Allocation).");

            _cacheAllocators.Add(new CacheMemoryAllocator(CacheSize));
            _cacheEntries.Add([]);

            // Without this the new region has no unwind information registered, so a stack walk that
            // crosses code living in it has nothing to resolve against.
            if (OperatingSystem.IsWindows())
            {
                JitUnwindWindows.InstallAdditionalFunctionTable(newRegion.Pointer, CacheSize);
            }

            int allocOffsetNew = _cacheAllocators[_activeRegionIndex].Allocate(codeSize);
            if (allocOffsetNew < 0)
            {
                throw new OutOfMemoryException("Failed to allocate in new Cache Region!");
            }

            newRegion.ExpandIfNeeded((ulong)allocOffsetNew + (ulong)codeSize);
            return allocOffsetNew;
        }

        private static int AlignCodeSize(int codeSize)
        {
            return checked(codeSize + (CodeAlignment - 1)) & ~(CodeAlignment - 1);
        }

        private static void Add(int offset, int size, UnwindInfo unwindInfo)
        {
            CacheEntry entry = new(offset, size, unwindInfo);

            List<CacheEntry> entries = _cacheEntries[_activeRegionIndex];

            int index = entries.BinarySearch(entry);

            if (index < 0)
            {
                index = ~index;
            }

            entries.Insert(index, entry);
        }

        /// <summary>Finds the entry covering <paramref name="offset"/> inside one region.</summary>
        public static bool TryFind(int regionIndex, int offset, out CacheEntry entry, out int entryIndex)
        {
            lock (_lock)
            {
                if (regionIndex >= 0 && regionIndex < _cacheEntries.Count)
                {
                    List<CacheEntry> entries = _cacheEntries[regionIndex];

                    int index = entries.BinarySearch(new CacheEntry(offset, 0, default));

                    if (index < 0)
                    {
                        index = ~index - 1;
                    }

                    if (index >= 0)
                    {
                        entry = entries[index];
                        entryIndex = index;
                        return true;
                    }
                }
            }

            entry = default;
            entryIndex = 0;
            return false;
        }

        /// <summary>
        /// Finds the entry covering <paramref name="offset"/> within the region that starts at
        /// <paramref name="regionBase"/>. The unwind callback is handed the base address it was
        /// registered with, which is how it names the region it is asking about.
        /// </summary>
        public static bool TryFindByBase(nint regionBase, int offset, out CacheEntry entry)
        {
            int regionIndex;

            lock (_lock)
            {
                regionIndex = -1;

                for (int i = 0; i < _jitRegions.Count; i++)
                {
                    if (_jitRegions[i].Pointer == regionBase)
                    {
                        regionIndex = i;
                        break;
                    }
                }
            }

            if (regionIndex < 0)
            {
                entry = default;
                return false;
            }

            return TryFind(regionIndex, offset, out entry, out _);
        }
    }
}
