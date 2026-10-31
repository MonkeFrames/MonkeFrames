using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MonkeFrames.Editor.Replays;

// The cache owns asset handles; each replay puppet owns a lease. Do not release
// the game's catalog or another rig's AssetReference operation when a replay closes.
internal static class ReplayCosmeticCache
{
    internal sealed class Lease : IDisposable
    {
        internal object Key;
        public AsyncOperationHandle<GameObject> Handle;
        public void Dispose()
        {
            if (Key == null) return;
            if (entries.TryGetValue(Key, out var entry) && --entry.References == 0)
            {
                entries.Remove(Key);
                if (entry.Handle.IsValid()) Addressables.Release(entry.Handle);
            }
            Key = null;
        }
    }
    private sealed class Entry
    {
        public AsyncOperationHandle<GameObject> Handle;
        public int References;
    }
    private static readonly Dictionary<object, Entry> entries = new();

    public static Lease Acquire(object key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Handle = Addressables.LoadAssetAsync<GameObject>(key) };
            entries.Add(key, entry);
        }
        entry.References++;
        return new Lease { Key = key, Handle = entry.Handle };
    }
}
