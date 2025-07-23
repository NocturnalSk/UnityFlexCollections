# UnityFlexCollections

A lightweight collection of high-performance, allocation-free data structures designed for Unity using Burst and Unity.Mathematics. These containers are optimized for small to mid-sized data sets (≤256 items), making them ideal for real-time game logic, simulations, and high-frequency updates.

## ✨ Features

- 🚀 `FlexList<T>`: A simple, minimal list optimized for small structures (<=200 entries), ideal for hot paths.
- 🧠 `SimdFlexList<T>`: Burst-compatible unmanaged list with SIMD-friendly layout and slot reuse.
- 🧩 `FlexDict<TKey, TValue>` *(coming soon)*: Fixed-size key-value structure for fast lookups without GC overhead.
- ❌ No GC Allocations
- ✅ Unity.Burst-compatible
- 🔥 Designed for performance-sensitive gameplay systems (ECS, Mono, hybrid)

---

## 📦 Structures Overview

### `FlexList<T>`

> Lightweight replacement for `List<T>` when used in hot update paths or gameplay systems with fewer than 200 elements.

- Fully inlineable
- Stack-only variant available
- Optional memory alignment for `float3`, `quaternion`, etc.

### `SimdFlexList<T>`

> Optimized for small fixed-capacity lists with add/remove/compact access, backed by fixed-size unmanaged memory.

- Supports add/remove/set/index access
- Slot reuse with O(1) deletion
- `Sort`, `Compact`, `ToArray`, and `ForEach` supported
- Valid index tracking with a freelist

---

## 📚 Examples

```csharp
SimdFlexList<float3> list = new SimdFlexList<float3>();
int idx = list.Add(new float3(1, 2, 3));
list.RemoveAt(idx);
list.Compact();
list.Sort((a, b) => a.x.CompareTo(b.x));
```

---

## 🛠 Use Cases

- High-frequency game logic (soldier AI, grid updates, physics events)
- Unity MonoBehaviour-side simulation
- Client-side pathfinding, ECS behavior containers
- Mobile game performance optimization

---

## 🔮 Roadmap

- [x] `FlexList<T>`
- [x] `SimdFlexList<T>`
- [ ] `FlexDict<TKey, TValue>`
- [ ] Custom iterators
- [ ] Optional burst-safe enumerators
- [ ] Unity Editor visual debugging

---

## 📄 License

MIT License © 2025 [NocturnalSk]
